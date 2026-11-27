using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Diagnostics;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    private static readonly string[] PortalCullGpuProfilerPath = ["Advanced", "Visibility", "PortalCull"];
    private static readonly string[] PortalCarveGpuProfilerPath = ["Advanced", "Visibility", "PortalCarve"];
    private static readonly string[] PortalRasterGpuProfilerPath = ["Advanced", "Visibility", "PortalRaster"];
    private static readonly string[] PortalDepthRestoreGpuProfilerPath = ["Advanced", "Visibility", "PortalDepthRestore"];
    private static readonly string[] PortalUnwindGpuProfilerPath = ["Advanced", "LatePasses", "PortalUnwind"];

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct AdvancedPortalGlassPushConstants(
        Vector3 corner0,
        uint parentViewIndex,
        Vector3 corner1,
        float fresnelStrength,
        Vector3 corner2,
        float specularStrength,
        Vector3 corner3,
        uint portalViewIndex,
        Vector4 glassTint,
        Vector3 planeNormal,
        uint flags)
    {
        public readonly Vector3 Corner0 = corner0;
        public readonly uint ParentViewIndex = parentViewIndex;
        public readonly Vector3 Corner1 = corner1;
        public readonly float FresnelStrength = fresnelStrength;
        public readonly Vector3 Corner2 = corner2;
        public readonly float SpecularStrength = specularStrength;
        public readonly Vector3 Corner3 = corner3;
        public readonly uint PortalViewIndex = portalViewIndex;
        public readonly Vector4 GlassTint = glassTint;
        public readonly Vector3 PlaneNormal = planeNormal;
        public readonly uint Flags = flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct AdvancedPortalQuadPushConstants(
        Vector3 corner0,
        uint parentViewIndex,
        Vector3 corner1,
        uint portalViewIndex,
        Vector3 corner2,
        float farDepth,
        Vector3 corner3,
        uint flags)
    {
        public readonly Vector3 Corner0 = corner0;
        public readonly uint ParentViewIndex = parentViewIndex;
        public readonly Vector3 Corner1 = corner1;
        public readonly uint PortalViewIndex = portalViewIndex;
        public readonly Vector3 Corner2 = corner2;
        public readonly float FarDepth = farDepth;
        public readonly Vector3 Corner3 = corner3;
        public readonly uint Flags = flags;
    }

    /// <summary>
    /// Records single-phase visibility preparation and outside-in stencil-masked portal
    /// rasterization for every scheduled mirror or portal sub-view in the frame plan.
    /// Runs before multisample visibility resolve into the active visibility target.
    /// </summary>
    private unsafe int RecordAdvancedPortalVisibilityPayload(
        scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedVisibilityOperationPayload payload,
        in VulkanPrimaryOperationRecordingInfo info)
    {
        if (payload.Request.Phase != EAdvancedVisibilityStageBackendPhase.PortalVisibility)
            throw new VulkanPlanPreconditionException(
                "Portal visibility recording received a non-portal physical phase.");

        AdvancedPortalFramePlan? portalPlan = payload.Input.PortalFramePlan;
        if (portalPlan is not { Count: > 0 } ||
            !DeviceContext.TryValidateShaderClipDistanceForPortalMirrors(out _))
        {
            return info.OperationIndex;
        }

        FramePlan framePlan = state.FramePlan
            ?? throw new VulkanPlanPreconditionException(
                "Advanced portal visibility reached recording without an accepted frame plan.");
        VulkanPreparedStableBinStream bins = framePlan.GetAdvancedVisibilityFamilyBins(
            payload.Request.Reservation, allowCreate: false);
        VulkanAdvancedPortalVisibilityState portalResources = payload.PortalVisibilityResources;
        VulkanVisibilityRasterPipeline carvePipeline = payload.PortalStencilCarvePipeline;
        VulkanVisibilityRasterPipeline depthResetPipeline = payload.PortalDepthResetPipeline;
        VulkanVisibilityRasterPipeline depthOnlyResetPipeline = payload.PortalDepthOnlyResetPipeline;
        if (!payload.State.IsValid || !payload.SceneState.IsValid ||
            !payload.TargetClosure.IsValid || !bins.HasSealedSubmissionPlans ||
            !portalResources.IsValid || !carvePipeline.IsValid ||
            !depthResetPipeline.IsValid || !depthOnlyResetPipeline.IsValid ||
            payload.EarlyVisibilityProgram is not { IsLinked: true } early ||
            payload.BuildIndirectProgram is not { IsLinked: true } indirect ||
            payload.EarlyIndexedGroupFinalizeProgram is not { IsLinked: true } groupFinalize ||
            early.LinkGeneration != payload.EarlyVisibilityLinkGeneration ||
            indirect.LinkGeneration != payload.BuildIndirectLinkGeneration ||
            groupFinalize.LinkGeneration != payload.EarlyIndexedGroupFinalizeLinkGeneration ||
            payload.EarlyVisibilityPipeline.Handle == 0 ||
            payload.BuildIndirectPipeline.Handle == 0 ||
            payload.EarlyIndexedGroupFinalizePipeline.Handle == 0 ||
            !info.BeginsRendering)
        {
            throw new VulkanPlanPreconditionException(
                "Advanced portal visibility reached recording without its sealed segment pool, compute programs, or quad pipelines.");
        }

        if (carvePipeline.Program.LinkGeneration != carvePipeline.ProgramLinkGeneration ||
            depthResetPipeline.Program.LinkGeneration != depthResetPipeline.ProgramLinkGeneration ||
            depthOnlyResetPipeline.Program.LinkGeneration != depthOnlyResetPipeline.ProgramLinkGeneration ||
            carvePipeline.TargetClosure != payload.TargetClosure ||
            depthResetPipeline.TargetClosure != payload.TargetClosure ||
            depthOnlyResetPipeline.TargetClosure != payload.TargetClosure)
        {
            throw new VulkanPlanPreconditionException(
                "Advanced portal quad pipeline closure changed after frame-plan sealing.");
        }

        if (ResourceRuntime.BackendObjects.Get(payload.Request.Target) is not VkFrameBuffer targetWrapper)
            throw new VulkanPlanPreconditionException(
                "Advanced portal visibility lost its authoritative Vulkan framebuffer wrapper.");
        if (state.Policy.AllowSynchronousResourceUploads)
            targetWrapper.EnsureCurrent();
        if (!targetWrapper.TryCaptureRecordedRenderTargetSnapshot(
                out VulkanRecordedRenderTargetSnapshot currentTarget) ||
            currentTarget != payload.TargetClosure.NativeTarget)
        {
            string mismatch = currentTarget.IsComplete
                ? payload.TargetClosure.NativeTarget.DescribeFirstMismatch(in currentTarget)
                : "the current native target is incomplete";
            throw new VulkanPlanPreconditionException(
                $"Advanced portal visibility target changed after sealing: {mismatch}.");
        }

        if (state.RenderScope.IsActive)
            EndActiveRenderPass(ref state);

        VulkanAdvancedVisibilityResourceState primaryState = payload.State;
        VulkanAdvancedVisibilityResourceState portalRasterState =
            portalResources.CreateRasterResourceState(in primaryState);
        VulkanAdvancedVisibilityOperationPayload portalPayload = payload with
        {
            State = portalRasterState,
        };

        // Run single-phase visibility preparation once across all scheduled portal views before
        // opening the graphics pass. Each portal view writes disjoint indirect/range segments.
        uint groups = DivideRoundUp(payload.State.PayloadCapacity, 256u);
        uint portalViewCount = checked((uint)portalPlan.Count);
        uint firstPortalViewIndex = checked((uint)portalPlan.PrimaryViewCount);
        if (groups != 0u && payload.State.PayloadCapacity != 0u)
        {
            using VulkanGpuProfilerScope portalCullScope = TryBeginVulkanGpuProfilerScope(
                state.CommandBuffer, PortalCullGpuProfilerPath);
            ShaderStageFlags pushStages =
                VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(DeviceContext);
            AdvancedVisibilityPreparationPushConstants portalPrepPush = new(
                firstPortalViewIndex,
                0u,
                payload.State.PayloadCapacity,
                0u);

            CmdBeginLabel(state.CommandBuffer, "Advanced.PortalVisibility.Prepare");
            BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Compute, payload.EarlyVisibilityPipeline);
            BindAdvancedVisibilityDescriptorSets(
                state.CommandBuffer,
                PipelineBindPoint.Compute,
                early.PipelineLayout,
                in portalPayload,
                portalResources.DescriptorSet);
            PushConstantsTracked(state.CommandBuffer, early.PipelineLayout, pushStages, 0u, portalPrepPush);
            Api.CmdDispatch(state.CommandBuffer, groups, portalViewCount, 1u);

            EmitMemoryBarrierMask(state.CommandBuffer, EMemoryBarrierMask.ShaderStorage);

            BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Compute, payload.BuildIndirectPipeline);
            BindAdvancedVisibilityDescriptorSets(
                state.CommandBuffer,
                PipelineBindPoint.Compute,
                indirect.PipelineLayout,
                in portalPayload,
                portalResources.DescriptorSet);
            PushConstantsTracked(state.CommandBuffer, indirect.PipelineLayout, pushStages, 0u, portalPrepPush);
            Api.CmdDispatch(state.CommandBuffer, groups, portalViewCount, 1u);

            if (payload.State.IndexedInstanceGroupCount != 0u)
            {
                EmitMemoryBarrierMask(state.CommandBuffer, EMemoryBarrierMask.ShaderStorage);
                BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Compute, payload.EarlyIndexedGroupFinalizePipeline);
                BindAdvancedVisibilityDescriptorSets(
                    state.CommandBuffer,
                    PipelineBindPoint.Compute,
                    groupFinalize.PipelineLayout,
                    in portalPayload,
                    portalResources.DescriptorSet);
                PushConstantsTracked(state.CommandBuffer, groupFinalize.PipelineLayout, pushStages, 0u, portalPrepPush);
                Api.CmdDispatch(
                    state.CommandBuffer,
                    DivideRoundUp(payload.State.IndexedInstanceGroupCount, 256u),
                    portalViewCount,
                    1u);
            }
            CmdEndLabel(state.CommandBuffer);
        }

        EmitAdvancedVisibilityRasterReadBarrier(ref state);

        BeginRenderPassForTarget(
            ref state,
            payload.Request.Target,
            info.PassIndex,
            state.ActiveContext,
            clearPolicy: payload.TargetClosure.ClearPolicy);
        if (!state.RenderScope.IsActive ||
            state.RenderScope.Target != payload.TargetClosure.Target ||
            state.RenderScope.UsesDynamicRendering != payload.TargetClosure.UsesDynamicRendering ||
            state.RenderScope.DepthStencilReadOnly != payload.TargetClosure.DepthStencilReadOnly ||
            payload.TargetClosure.UsesDynamicRendering &&
                !state.RenderScope.DynamicRenderingFormats.Equals(
                    payload.TargetClosure.DynamicRenderingFormats) ||
            !payload.TargetClosure.UsesDynamicRendering &&
                state.RenderScope.RenderPass.Handle != payload.TargetClosure.RenderPass.Handle)
        {
            throw new VulkanPlanPreconditionException(
                "The active portal visibility render scope does not match its sealed target closure.");
        }

        Extent2D extent = new(
            payload.TargetClosure.NativeTarget.Width,
            payload.TargetClosure.NativeTarget.Height);
        Viewport viewport = CreateVulkanViewport(extent);
        Rect2D fullScissor = new(new Offset2D(0, 0), extent);
        float farDepth = payload.TargetClosure.ClearPolicy.ReversedDepth ? 0.0f : 1.0f;
        ShaderStageFlags graphicsPushStages =
            VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(DeviceContext);
        ReadOnlySpan<AdvancedScheduledPortalView> scheduledNodes = portalPlan.ScheduledNodes;

        CmdBeginLabel(state.CommandBuffer, "Advanced.PortalVisibility.OutsideIn");
        for (int depth = 1; depth <= portalPlan.MaxScheduledDepth; ++depth)
        {
            using (VulkanGpuProfilerScope portalCarveScope = TryBeginVulkanGpuProfilerScope(
                state.CommandBuffer, PortalCarveGpuProfilerPath))
            {
                // 1. Stencil Carve for every portal node at this recursion depth.
                BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Graphics, carvePipeline.Pipeline);
                BindAdvancedVisibilityDescriptorSets(
                    state.CommandBuffer,
                    PipelineBindPoint.Graphics,
                    carvePipeline.PipelineLayout,
                    in portalPayload,
                    portalResources.DescriptorSet);
                for (int nodeIndex = 0; nodeIndex < scheduledNodes.Length; ++nodeIndex)
                {
                    ref readonly AdvancedScheduledPortalView node = ref scheduledNodes[nodeIndex];
                    if (node.RecursionDepth != (uint)depth)
                        continue;

                    Rect2D nodeScissor = ToClampedScissorRect(node.ScissorRect, extent);
                    if (nodeScissor.Extent.Width == 0u || nodeScissor.Extent.Height == 0u)
                        continue;

                    SetViewportScissorTracked(state.CommandBuffer, in viewport, in nodeScissor);
                    Api.CmdSetStencilReference(
                        state.CommandBuffer,
                        StencilFaceFlags.FaceFrontAndBack,
                        AdvancedStencilBitLayout.EncodePortalNode(node.ParentStencilNodeId));
                    Api.CmdSetStencilWriteMask(
                        state.CommandBuffer,
                        StencilFaceFlags.FaceFrontAndBack,
                        AdvancedStencilBitLayout.ComputePortalTransitionWriteMask(
                            node.ParentStencilNodeId,
                            node.StencilNodeId));
                    PushConstantsTracked(
                        state.CommandBuffer,
                        carvePipeline.PipelineLayout,
                        graphicsPushStages,
                        0u,
                        new AdvancedPortalQuadPushConstants(
                            node.Surface.QuadCorner0,
                            node.ParentViewIndex,
                            node.Surface.QuadCorner1,
                            node.ViewIndex,
                            node.Surface.QuadCorner2,
                            farDepth,
                            node.Surface.QuadCorner3,
                            flags: 0u));
                    Api.CmdDraw(state.CommandBuffer, 4u, 1u, 0u, 0u);
                }

                // 2. Depth and visibility reset on newly carved portal pixels.
                // Transparent-background mirrors reset only hardware depth so primary visibility
                // attachments remain intact wherever reflected geometry does not overwrite them.
                int boundDepthResetVariant = 0;
                for (int nodeIndex = 0; nodeIndex < scheduledNodes.Length; ++nodeIndex)
                {
                    ref readonly AdvancedScheduledPortalView node = ref scheduledNodes[nodeIndex];
                    if (node.RecursionDepth != (uint)depth)
                        continue;

                    Rect2D nodeScissor = ToClampedScissorRect(node.ScissorRect, extent);
                    if (nodeScissor.Extent.Width == 0u || nodeScissor.Extent.Height == 0u)
                        continue;

                    int desiredVariant = node.Surface.TransparentBackground ? 2 : 1;
                    VulkanVisibilityRasterPipeline activeResetPipeline = desiredVariant == 2
                        ? depthOnlyResetPipeline
                        : depthResetPipeline;
                    if (boundDepthResetVariant != desiredVariant)
                    {
                        BindPipelineTracked(
                            state.CommandBuffer,
                            PipelineBindPoint.Graphics,
                            activeResetPipeline.Pipeline);
                        BindAdvancedVisibilityDescriptorSets(
                            state.CommandBuffer,
                            PipelineBindPoint.Graphics,
                            activeResetPipeline.PipelineLayout,
                            in portalPayload,
                            portalResources.DescriptorSet);
                        boundDepthResetVariant = desiredVariant;
                    }

                    SetViewportScissorTracked(state.CommandBuffer, in viewport, in nodeScissor);
                    Api.CmdSetStencilReference(
                        state.CommandBuffer,
                        StencilFaceFlags.FaceFrontAndBack,
                        AdvancedStencilBitLayout.EncodePortalNode(node.StencilNodeId));
                    PushConstantsTracked(
                        state.CommandBuffer,
                        activeResetPipeline.PipelineLayout,
                        graphicsPushStages,
                        0u,
                        new AdvancedPortalQuadPushConstants(
                            node.Surface.QuadCorner0,
                            node.ParentViewIndex,
                            node.Surface.QuadCorner1,
                            node.ViewIndex,
                            node.Surface.QuadCorner2,
                            farDepth,
                            node.Surface.QuadCorner3,
                            flags: 1u));
                    Api.CmdDraw(state.CommandBuffer, 3u, 1u, 0u, 0u);
                }
            }

            // 3. Reflected or portal-transformed visibility raster for each node at this depth.
            if (payload.State.PayloadCapacity == 0u)
                continue;

            using (VulkanGpuProfilerScope portalRasterScope = TryBeginVulkanGpuProfilerScope(
                state.CommandBuffer, PortalRasterGpuProfilerPath))
            {
                for (int nodeIndex = 0; nodeIndex < scheduledNodes.Length; ++nodeIndex)
                {
                    ref readonly AdvancedScheduledPortalView node = ref scheduledNodes[nodeIndex];
                    if (node.RecursionDepth != (uint)depth)
                        continue;

                    Rect2D nodeScissor = ToClampedScissorRect(node.ScissorRect, extent);
                    if (nodeScissor.Extent.Width == 0u || nodeScissor.Extent.Height == 0u)
                        continue;

                    SetViewportScissorTracked(state.CommandBuffer, in viewport, in nodeScissor);
                    Api.CmdSetStencilReference(
                        state.CommandBuffer,
                        StencilFaceFlags.FaceFrontAndBack,
                        AdvancedStencilBitLayout.EncodePortalNode(node.StencilNodeId));
                    RecordAdvancedPortalNodeBins(
                        ref state,
                        in portalPayload,
                        in portalResources,
                        bins,
                        checked((uint)nodeIndex),
                        in node);
                }
            }
        }
        CmdEndLabel(state.CommandBuffer);

        SetViewportScissorTracked(state.CommandBuffer, in viewport, in fullScissor);

        if (HasTransparentPortalNode(portalPlan) && payload.PortalTransparentDepthRestorePipeline.IsValid)
        {
            RecordAdvancedPortalTransparentDepthRestore(
                ref state,
                in payload,
                portalPlan,
                extent,
                in viewport,
                in fullScissor,
                farDepth,
                graphicsPushStages);
        }

        return info.OperationIndex;
    }

    private static bool HasTransparentPortalNode(AdvancedPortalFramePlan portalPlan)
    {
        ReadOnlySpan<AdvancedScheduledPortalView> nodes = portalPlan.ScheduledNodes;
        for (int i = 0; i < nodes.Length; ++i)
            if (nodes[i].Surface.TransparentBackground)
                return true;
        return false;
    }

    /// <summary>
    /// Restores primary scene hardware depth on uncovered pixels inside transparent-background
    /// in-place portals. Ends the visibility render pass, transitions visibility identity and
    /// metadata to shader-readable general layout, and runs a zero-color-attachment dynamic
    /// rendering pass that writes reconstructed primary depth where stencil matches each portal
    /// node and visibility metadata still belongs to the primary view.
    /// </summary>
    private unsafe void RecordAdvancedPortalTransparentDepthRestore(
        scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedVisibilityOperationPayload payload,
        AdvancedPortalFramePlan portalPlan,
        Extent2D extent,
        in Viewport viewport,
        in Rect2D fullScissor,
        float farDepth,
        ShaderStageFlags graphicsPushStages)
    {
        VulkanVisibilityRasterPipeline restorePipeline = payload.PortalTransparentDepthRestorePipeline;
        if (!restorePipeline.IsValid ||
            restorePipeline.Program.LinkGeneration != restorePipeline.ProgramLinkGeneration ||
            restorePipeline.TargetClosure != payload.TargetClosure ||
            !payload.TargetClosure.UsesDynamicRendering)
        {
            throw new VulkanPlanPreconditionException(
                "Advanced portal transparent depth restore pipeline closure changed after frame-plan sealing.");
        }

        if (payload.PortalDepthRestoreClosure0 is not { IsValid: true } closure0 ||
            payload.PortalDepthRestoreDescriptorSet0.Handle == 0)
        {
            return;
        }

        ImageView depthStencilView = default;
        Image depthStencilImage = default;
        VulkanRecordedRenderTargetSnapshot nativeTarget = payload.TargetClosure.NativeTarget;
        for (int i = 0; i < nativeTarget.AttachmentCount; ++i)
        {
            VulkanNativeAttachmentIdentity attachment = nativeTarget.GetAttachment(i);
            if (attachment.ExpectedLayout is ImageLayout.DepthStencilAttachmentOptimal or ImageLayout.DepthStencilReadOnlyOptimal)
            {
                depthStencilView = new ImageView(attachment.ImageViewHandle);
                depthStencilImage = new Image(attachment.ImageHandle);
                break;
            }
        }
        if (depthStencilView.Handle == 0 || depthStencilImage.Handle == 0)
            return;

        if (state.RenderScope.IsActive)
            EndActiveRenderPass(ref state);

        using VulkanGpuProfilerScope restoreScope = TryBeginVulkanGpuProfilerScope(
            state.CommandBuffer, PortalDepthRestoreGpuProfilerPath);
        CmdBeginLabel(state.CommandBuffer, "Advanced.PortalVisibility.TransparentDepthRestore");

        bool usesMsaa = payload.TargetClosure.RasterizationSamples != SampleCountFlags.Count1Bit;
        VulkanPhysicalImageGroup identityGroup = usesMsaa ? closure0.IdentityMultisample! : closure0.Identity;
        VulkanPhysicalImageGroup metadataGroup = usesMsaa ? closure0.MetadataMultisample! : closure0.Metadata;
        for (uint viewIndex = 0u; viewIndex < payload.State.ViewCount; ++viewIndex)
        {
            EmitAdvancedVisibilityImageBarrier(
                state.CommandBuffer, identityGroup, 0u, viewIndex,
                ImageLayout.General, AccessFlags.ShaderReadBit,
                PipelineStageFlags.FragmentShaderBit, allowUndefined: false);
            EmitAdvancedVisibilityImageBarrier(
                state.CommandBuffer, metadataGroup, 0u, viewIndex,
                ImageLayout.General, AccessFlags.ShaderReadBit,
                PipelineStageFlags.FragmentShaderBit, allowUndefined: false);
        }

        ImageMemoryBarrier depthBarrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
            OldLayout = ImageLayout.DepthStencilAttachmentOptimal,
            NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = depthStencilImage,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
                BaseMipLevel = 0u,
                LevelCount = 1u,
                BaseArrayLayer = 0u,
                LayerCount = Math.Max(1u, payload.State.ViewCount),
            },
        };
        Api.CmdPipelineBarrier(
            state.CommandBuffer,
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            0,
            0u, null,
            0u, null,
            1u, &depthBarrier);

        RenderingAttachmentInfo depthAttachment = new()
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = depthStencilView,
            ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };
        RenderingAttachmentInfo stencilAttachment = depthAttachment;
        RenderingInfo renderingInfo = new()
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), extent),
            LayerCount = Math.Max(1u, payload.TargetClosure.DynamicRenderingFormats.LayerCount),
            ViewMask = payload.TargetClosure.DynamicRenderingFormats.ViewMask,
            ColorAttachmentCount = 0u,
            PColorAttachments = null,
            PDepthAttachment = &depthAttachment,
            PStencilAttachment = &stencilAttachment,
        };
        CmdBeginDynamicRendering(state.CommandBuffer, &renderingInfo, preferKhrDynamicRendering: false);

        BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Graphics, restorePipeline.Pipeline);
        ReadOnlySpan<AdvancedScheduledPortalView> scheduledNodes = portalPlan.ScheduledNodes;
        for (uint eye = 0u; eye < payload.State.ViewCount; ++eye)
        {
            VulkanAdvancedNativeComputeClosure? eyeClosure = eye == 0u
                ? payload.PortalDepthRestoreClosure0
                : payload.PortalDepthRestoreClosure1;
            DescriptorSet eyeDescriptorSet = eye == 0u
                ? payload.PortalDepthRestoreDescriptorSet0
                : payload.PortalDepthRestoreDescriptorSet1;
            if (eyeClosure is not { IsValid: true } activeEyeClosure || eyeDescriptorSet.Handle == 0)
                continue;

            bool eyeBound = false;
            for (int nodeIndex = 0; nodeIndex < scheduledNodes.Length; ++nodeIndex)
            {
                ref readonly AdvancedScheduledPortalView node = ref scheduledNodes[nodeIndex];
                if (!node.Surface.TransparentBackground || node.PrimaryViewIndex != eye)
                    continue;

                Rect2D nodeScissor = ToClampedScissorRect(node.ScissorRect, extent);
                if (nodeScissor.Extent.Width == 0u || nodeScissor.Extent.Height == 0u)
                    continue;

                if (!eyeBound)
                {
                    TrackAdvancedNativeShadeClosure(state.CommandBuffer, in activeEyeClosure);
                    BindAdvancedVisibilityDescriptorSets(
                        state.CommandBuffer,
                        PipelineBindPoint.Graphics,
                        restorePipeline.PipelineLayout,
                        in payload,
                        eyeDescriptorSet);
                    eyeBound = true;
                }

                SetViewportScissorTracked(state.CommandBuffer, in viewport, in nodeScissor);
                Api.CmdSetStencilReference(
                    state.CommandBuffer,
                    StencilFaceFlags.FaceFrontAndBack,
                    AdvancedStencilBitLayout.EncodePortalNode(node.StencilNodeId));
                PushConstantsTracked(
                    state.CommandBuffer,
                    restorePipeline.PipelineLayout,
                    graphicsPushStages,
                    0u,
                    new AdvancedPortalQuadPushConstants(
                        node.Surface.QuadCorner0,
                        node.ParentViewIndex,
                        node.Surface.QuadCorner1,
                        node.ViewIndex,
                        node.Surface.QuadCorner2,
                        farDepth,
                        node.Surface.QuadCorner3,
                        flags: 1u));
                Api.CmdDraw(state.CommandBuffer, 3u, 1u, 0u, 0u);
            }
        }

        SetViewportScissorTracked(state.CommandBuffer, in viewport, in fullScissor);
        CmdEndDynamicRendering(state.CommandBuffer, preferKhrDynamicRendering: false);
        CmdEndLabel(state.CommandBuffer);
    }

    /// <summary>
    /// Records deepest-out portal unwind on <c>ForwardPassFBO</c> at the start of
    /// <see cref="EAdvancedRenderStage.LatePasses"/>: renders reflected sky on uncovered far-depth
    /// mirror pixels, blends mirror glass tint/Fresnel/specular, restores real mirror plane depth,
    /// and unwinds stencil bits 2..6 to the parent node ID (ending at 0).
    /// </summary>
    private unsafe int RecordAdvancedPortalUnwindPayload(
        scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedVisibilityOperationPayload payload,
        in VulkanPrimaryOperationRecordingInfo info)
    {
        if (!payload.Request.IsPortalLatePhase)
            throw new VulkanPlanPreconditionException(
                "Portal unwind recording received a non-portal-unwind physical phase.");

        AdvancedPortalFramePlan? portalPlan = payload.Input.PortalFramePlan;
        if (portalPlan is not { Count: > 0 })
            return info.OperationIndex;

        bool skyOnly = payload.Request.IsPortalSkyUnwindPhase;
        bool drawSky = skyOnly || !payload.SkipPortalSkyInUnwind;
        VulkanVisibilityRasterPipeline skyPipeline = payload.PortalSkyUnwindPipeline;
        VulkanVisibilityRasterPipeline glassPipeline = payload.PortalGlassCompositePipeline;
        VulkanVisibilityRasterPipeline transparentGlassPipeline = payload.PortalTransparentGlassCompositePipeline;
        if (!payload.State.IsValid || !payload.SceneState.IsValid ||
            !payload.TargetClosure.IsValid ||
            payload.TargetClosure.Kind != EVulkanAdvancedVisibilityTargetKind.PortalUnwind ||
            !info.BeginsRendering ||
            (drawSky && (!skyPipeline.IsValid ||
                skyPipeline.Program.LinkGeneration != skyPipeline.ProgramLinkGeneration ||
                skyPipeline.TargetClosure != payload.TargetClosure)) ||
            (!skyOnly && (!glassPipeline.IsValid ||
                glassPipeline.Program.LinkGeneration != glassPipeline.ProgramLinkGeneration ||
                glassPipeline.TargetClosure != payload.TargetClosure ||
                !transparentGlassPipeline.IsValid ||
                transparentGlassPipeline.Program.LinkGeneration != transparentGlassPipeline.ProgramLinkGeneration ||
                transparentGlassPipeline.TargetClosure != payload.TargetClosure)))
        {
            throw new VulkanPlanPreconditionException(
                "Advanced portal unwind reached recording without its sealed target closure or unwind pipelines.");
        }

        if (ResourceRuntime.BackendObjects.Get(payload.Request.Target) is not VkFrameBuffer targetWrapper)
            throw new VulkanPlanPreconditionException(
                "Advanced portal unwind lost its authoritative Vulkan framebuffer wrapper.");
        if (state.Policy.AllowSynchronousResourceUploads)
            targetWrapper.EnsureCurrent();
        if (!targetWrapper.TryCaptureRecordedRenderTargetSnapshot(
                out VulkanRecordedRenderTargetSnapshot currentTarget) ||
            currentTarget != payload.TargetClosure.NativeTarget)
        {
            string mismatch = currentTarget.IsComplete
                ? payload.TargetClosure.NativeTarget.DescribeFirstMismatch(in currentTarget)
                : "the current native target is incomplete";
            throw new VulkanPlanPreconditionException(
                $"Advanced portal unwind target changed after sealing: {mismatch}.");
        }

        if (state.RenderScope.IsActive)
            EndActiveRenderPass(ref state);

        using VulkanGpuProfilerScope unwindScope = TryBeginVulkanGpuProfilerScope(
            state.CommandBuffer, PortalUnwindGpuProfilerPath);

        BeginRenderPassForTarget(
            ref state,
            payload.Request.Target,
            info.PassIndex,
            state.ActiveContext,
            clearPolicy: payload.TargetClosure.ClearPolicy);

        Extent2D extent = new(
            payload.TargetClosure.NativeTarget.Width,
            payload.TargetClosure.NativeTarget.Height);
        Viewport viewport = CreateVulkanViewport(extent);
        Rect2D fullScissor = new(new Offset2D(0, 0), extent);
        float farDepth = payload.TargetClosure.ClearPolicy.ReversedDepth ? 0.0f : 1.0f;
        ShaderStageFlags graphicsPushStages =
            VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(DeviceContext);
        ReadOnlySpan<AdvancedScheduledPortalView> scheduledNodes = portalPlan.ScheduledNodes;

        CmdBeginLabel(state.CommandBuffer, skyOnly ? "Advanced.PortalSkyUnwind" : "Advanced.PortalUnwind");
        for (int depth = portalPlan.MaxScheduledDepth; depth >= 1; --depth)
        {
            if (drawSky)
            {
                BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Graphics, skyPipeline.Pipeline);
                BindAdvancedVisibilityDescriptorSets(
                    state.CommandBuffer,
                    PipelineBindPoint.Graphics,
                    skyPipeline.PipelineLayout,
                    in payload,
                    payload.State.DescriptorSet);
                for (int nodeIndex = 0; nodeIndex < scheduledNodes.Length; ++nodeIndex)
                {
                    ref readonly AdvancedScheduledPortalView node = ref scheduledNodes[nodeIndex];
                    if (node.RecursionDepth != (uint)depth || node.Surface.TransparentBackground)
                        continue;

                    Rect2D nodeScissor = ToClampedScissorRect(node.ScissorRect, extent);
                    if (nodeScissor.Extent.Width == 0u || nodeScissor.Extent.Height == 0u)
                        continue;

                    SetViewportScissorTracked(state.CommandBuffer, in viewport, in nodeScissor);
                    Api.CmdSetStencilReference(
                        state.CommandBuffer,
                        StencilFaceFlags.FaceFrontAndBack,
                        AdvancedStencilBitLayout.EncodePortalNode(node.StencilNodeId));
                    PushConstantsTracked(
                        state.CommandBuffer,
                        skyPipeline.PipelineLayout,
                        graphicsPushStages,
                        0u,
                        new AdvancedPortalQuadPushConstants(
                            node.Surface.QuadCorner0,
                            node.ParentViewIndex,
                            node.Surface.QuadCorner1,
                            node.ViewIndex,
                            node.Surface.QuadCorner2,
                            farDepth,
                            node.Surface.QuadCorner3,
                            flags: 1u));
                    Api.CmdDraw(state.CommandBuffer, 3u, 1u, 0u, 0u);
                }
            }

            if (skyOnly)
                continue;

            int boundGlassVariant = 0;
            for (int nodeIndex = 0; nodeIndex < scheduledNodes.Length; ++nodeIndex)
            {
                ref readonly AdvancedScheduledPortalView node = ref scheduledNodes[nodeIndex];
                if (node.RecursionDepth != (uint)depth)
                    continue;

                Rect2D nodeScissor = ToClampedScissorRect(node.ScissorRect, extent);
                if (nodeScissor.Extent.Width == 0u || nodeScissor.Extent.Height == 0u)
                    continue;

                int desiredGlassVariant = node.Surface.TransparentBackground ? 2 : 1;
                VulkanVisibilityRasterPipeline activeGlassPipeline = desiredGlassVariant == 2
                    ? transparentGlassPipeline
                    : glassPipeline;
                if (boundGlassVariant != desiredGlassVariant)
                {
                    BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Graphics, activeGlassPipeline.Pipeline);
                    BindAdvancedVisibilityDescriptorSets(
                        state.CommandBuffer,
                        PipelineBindPoint.Graphics,
                        activeGlassPipeline.PipelineLayout,
                        in payload,
                        payload.State.DescriptorSet);
                    boundGlassVariant = desiredGlassVariant;
                }

                SetViewportScissorTracked(state.CommandBuffer, in viewport, in nodeScissor);
                Api.CmdSetStencilReference(
                    state.CommandBuffer,
                    StencilFaceFlags.FaceFrontAndBack,
                    AdvancedStencilBitLayout.EncodePortalNode(node.StencilNodeId));
                Api.CmdSetStencilWriteMask(
                    state.CommandBuffer,
                    StencilFaceFlags.FaceFrontAndBack,
                    AdvancedStencilBitLayout.ComputePortalTransitionWriteMask(
                        node.StencilNodeId,
                        node.ParentStencilNodeId));
                PushConstantsTracked(
                    state.CommandBuffer,
                    activeGlassPipeline.PipelineLayout,
                    graphicsPushStages,
                    0u,
                    new AdvancedPortalGlassPushConstants(
                        node.Surface.QuadCorner0,
                        node.ParentViewIndex,
                        node.Surface.QuadCorner1,
                        node.Surface.FresnelStrength,
                        node.Surface.QuadCorner2,
                        node.Surface.SpecularStrength,
                        node.Surface.QuadCorner3,
                        node.ViewIndex,
                        node.Surface.GlassTint,
                        node.Surface.PlaneNormal,
                        flags: 0u));
                Api.CmdDraw(state.CommandBuffer, 4u, 1u, 0u, 0u);
            }
        }
        CmdEndLabel(state.CommandBuffer);

        SetViewportScissorTracked(state.CommandBuffer, in viewport, in fullScissor);
        return info.OperationIndex;
    }

    private unsafe void RecordAdvancedPortalNodeBins(
        scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedVisibilityOperationPayload portalPayload,
        in VulkanAdvancedPortalVisibilityState portalResources,
        VulkanPreparedStableBinStream bins,
        uint portalSlotIndex,
        in AdvancedScheduledPortalView node)
    {
        if (!portalResources.TryGetPortalViewSegment(portalSlotIndex, out uint payloadBase, out uint rangeBase))
            throw new VulkanPlanPreconditionException("Portal visibility raster could not resolve its sealed view segment.");

        ReadOnlySpan<VulkanPreparedStableBinHeader> headers = bins.Headers;
        ReadOnlySpan<VulkanPreparedStableBinRecord> allRecords = bins.Records;
        ShaderStageFlags pushStages =
            VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(DeviceContext);
        VulkanAdvancedVisibilityResourceState visibilityState = portalPayload.State;

        for (int headerIndex = 0; headerIndex < headers.Length; ++headerIndex)
        {
            ref readonly VulkanPreparedStableBinHeader header = ref headers[headerIndex];
            if (header.IsSurfaceDecal)
                continue;
            if (!header.IsPortalRasterReady)
                throw new VulkanPlanPreconditionException(
                    "A non-empty visibility bin has no sealed portal raster pipelines.");

            VulkanVisibilityRasterPipeline raster = node.InvertedWinding
                ? header.PortalInvertedRasterPipeline
                : header.PortalNormalRasterPipeline;
            if (!raster.IsValid ||
                raster.TargetClosure != portalPayload.TargetClosure ||
                raster.Program.LinkGeneration != raster.ProgramLinkGeneration)
            {
                throw new VulkanPlanPreconditionException(
                    "A portal visibility raster pipeline closure changed after frame-plan sealing.");
            }

            VulkanSealedBinSubmissionPlan plan = header.SubmissionPlan!;
            if (!plan.OutputPolicy.AllowsCanonicalVisibilityFamily ||
                plan.OutputPolicy.VisibilityReservation != portalPayload.Request.Reservation)
            {
                throw new VulkanPlanPreconditionException(
                    $"Portal visibility raster reached recording with an unsupported output policy: {plan.OutputPolicy.DescribeCanonicalVisibilityRejection()}.");
            }

            AdvancedIndirectRange indirectRange = header.IndirectRange;
            if (plan.ResolvedStrategy != EMeshSubmissionStrategy.CpuDirect)
            {
                try
                {
                    indirectRange = indirectRange with
                    {
                        FirstPayloadIndex = checked(indirectRange.FirstPayloadIndex + payloadBase),
                        ArgumentBufferOffset = checked(indirectRange.ArgumentBufferOffset + payloadBase * 20u),
                        CountBufferOffset = checked(indirectRange.CountBufferOffset + rangeBase * sizeof(uint)),
                    };
                }
                catch (OverflowException)
                {
                    throw new VulkanPlanPreconditionException(
                        "Portal visibility raster view segment overflowed the sealed set-1 ABI.");
                }
            }

            if (!VulkanStableBinSubmissionLowering.TryLower(
                    plan,
                    in header,
                    in indirectRange,
                    in visibilityState,
                    out VulkanStableBinSubmission submission,
                    out VulkanStableBinSubmissionLoweringFailure lowerFailure))
            {
                throw new VulkanPlanPreconditionException(
                    $"Portal visibility bin lowering failed after sealing: {lowerFailure}.");
            }

            BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Graphics, raster.Pipeline);
            BindAdvancedVisibilityDescriptorSets(
                state.CommandBuffer,
                PipelineBindPoint.Graphics,
                raster.PipelineLayout,
                in portalPayload,
                portalResources.DescriptorSet);

            VulkanResidentDrawTemplateNativeState native = header.NativeState;
            ReadOnlySpan<VulkanPreparedStableBinRecord> records =
                allRecords.Slice(header.RecordOffset, header.RecordCount);
            bool meshlet = raster.IsMeshShaderPipeline;
            VulkanVisibilityGeometryRecordClosure geometryClosure = records[0].VisibilityGeometryClosure;
            VulkanVisibilityPreparedVertexSource deformedVertices = bins.VisibilityGeometrySources.CurrentVertices;
            VulkanVisibilityPreparedVertexSource boundVertices = geometryClosure.ResolveVertexSource(in deformedVertices);
            if (!meshlet)
            {
                for (int bindingIndex = 0; bindingIndex < native.VertexBufferCount; ++bindingIndex)
                {
                    BindVertexBufferTracked(
                        state.CommandBuffer,
                        native.GetVertexBinding(bindingIndex),
                        native.GetVertexBuffer(bindingIndex),
                        boundVertices.Offset);
                }
                BindIndexBufferTracked(
                    state.CommandBuffer,
                    native.Primitive0.IndexBuffer,
                    geometryClosure.IndexSlice.Offset,
                    native.Primitive0.IndexType);
            }

            PushConstantsTracked(
                state.CommandBuffer,
                raster.PipelineLayout,
                pushStages,
                0u,
                new AdvancedVisibilityMeshRasterPushConstants(
                    indirectRange.FirstPayloadIndex,
                    (uint)indirectRange.Key.Producer,
                    node.ViewIndex,
                    1u));

            if (plan.ResolvedStrategy == EMeshSubmissionStrategy.CpuDirect)
            {
                RecordAdvancedVisibilityCpuDirect(state.CommandBuffer, records);
                continue;
            }

            if (!TryRecordStableBinSubmission(
                    state.CommandBuffer,
                    in submission,
                    null,
                    records,
                    out VulkanStableBinSubmissionRecordingFailure recordFailure))
            {
                throw new VulkanPlanPreconditionException(
                    $"Portal visibility bin recording failed after sealing: {recordFailure}.");
            }
        }
    }

    private static Rect2D ToClampedScissorRect(AdvancedPortalScissorRect scissor, Extent2D targetExtent)
    {
        int x = Math.Clamp(scissor.X, 0, checked((int)targetExtent.Width));
        int y = Math.Clamp(scissor.Y, 0, checked((int)targetExtent.Height));
        int maxWidth = Math.Max(0, checked((int)targetExtent.Width) - x);
        int maxHeight = Math.Max(0, checked((int)targetExtent.Height) - y);
        uint width = checked((uint)Math.Clamp(scissor.Width, 0, maxWidth));
        uint height = checked((uint)Math.Clamp(scissor.Height, 0, maxHeight));
        return new Rect2D(new Offset2D(x, y), new Extent2D(width, height));
    }
}
