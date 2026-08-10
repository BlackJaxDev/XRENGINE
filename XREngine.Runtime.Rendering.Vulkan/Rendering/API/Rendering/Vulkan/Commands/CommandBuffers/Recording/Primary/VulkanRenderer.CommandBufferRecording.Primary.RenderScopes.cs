using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    internal sealed unsafe partial class VulkanCommandRuntime
    {

        internal void EndActiveRenderPass(scoped ref PrimaryCommandBufferRecordingState recordingState, bool finalClose = false)
        {
            if (!recordingState.RenderScope.IsActive)
            {
                if (finalClose && !recordingState.PreserveSwapchainForOverlay)
                    TransitionSwapchainToPresent(ref recordingState);
                return;
            }

            bool transitionSwapchainToPresent = recordingState.RenderScope.UsesDynamicRendering && recordingState.RenderScope.Target is null;
            if (recordingState.ActiveInlineQuery is not null)
            {
                if (!recordingState.ActiveInlineQueryRecordedDraw)
                    recordingState.FrameOpsRequireRerecordLocal = true;
                recordingState.ActiveInlineQuery.EndQuery(recordingState.CommandBuffer);
                recordingState.ActiveInlineQuery.InvalidateRecordedResultEpoch(recordingState.CommandBuffer);
                Debug.VulkanWarningEvery(
                    $"Vulkan.InterruptedInlineQuery.{recordingState.ActiveInlineQuery.GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Inline occlusion query was interrupted by a render-scope transition; this epoch will resolve visible. Query='{0}'.",
                    recordingState.ActiveInlineQuery.Data.Name ?? "<unnamed>");
                recordingState.ActiveInlineQuery = null;
                recordingState.ActiveInlineQueryRecordedDraw = false;
            }

            if (recordingState.RenderScope.UsesDynamicRendering)
            {
                CmdEndDynamicRendering(
                    recordingState.CommandBuffer,
                    recordingState.Policy.PreferKhrDynamicRendering);

                if (transitionSwapchainToPresent)
                {
                    recordingState.SwapchainInColorAttachmentLayout = true;
                    recordingState.SwapchainFinalLayout = ImageLayout.ColorAttachmentOptimal;
                    if (finalClose && !recordingState.PreserveSwapchainForOverlay)
                        TransitionSwapchainToPresent(ref recordingState);
                }
                else if (recordingState.RenderScope.Target is not null && recordingState.RenderScope.AttachmentSignature is not null)
                {
                    VkFrameBuffer? vkFbo = GenericToAPI<VkFrameBuffer>(recordingState.RenderScope.Target);
                    if (vkFbo is not null)
                    {
                        // Dynamic rendering has just completed. Publish the attachment
                        // accesses (including store-op writes) before the final-layout
                        // barriers query the command-buffer-local synchronization state.
                        RecordFboAttachmentAccessState(
                            recordingState.CommandBuffer,
                            vkFbo,
                            recordingState.RenderScope.AttachmentSignature,
                            useReferenceLayouts: true);
                    }

                    TransitionFboAttachmentsForDynamicRendering(
                        recordingState.CommandBuffer,
                        recordingState.RenderScope.Target,
                        recordingState.RenderScope.AttachmentSignature,
                        beginRendering: false);

                    if (vkFbo is not null)
                    {
                        RecordFboAttachmentAccessState(
                            recordingState.CommandBuffer,
                            vkFbo,
                            recordingState.RenderScope.AttachmentSignature,
                            useReferenceLayouts: false);
                    }

                    ImageLayout[] finalLayouts = GetFboAttachmentLayoutScratch(recordingState.RenderScope.Target, recordingState.RenderScope.AttachmentSignature.Length);
                    VkFrameBuffer.WriteFinalLayouts(recordingState.RenderScope.AttachmentSignature, finalLayouts);
                }
            }
            else
            {
                // Update physical group layout tracking for FBO attachment images.
                // The render pass transitions each attachment from initialLayout to
                // finalLayout, so after CmdEndRenderPass the images are in their
                // finalLayout. We update the tracked layout so that subsequent blit
                // barriers use the correct OldLayout.
                if (recordingState.RenderScope.Target is not null)
                {
                    // Record the finalLayout of each attachment so the NEXT render
                    // pass on this FBO can set initialLayout correctly and preserve
                    // content across pass boundaries.
                    var vkFbo = GenericToAPI<VkFrameBuffer>(recordingState.RenderScope.Target);
                    if (vkFbo is not null)
                    {
                        int attachmentCount = recordingState.RenderScope.AttachmentSignature?.Length ?? (int)vkFbo.AttachmentCount;
                        ImageLayout[] finalLayouts = GetFboAttachmentLayoutScratch(recordingState.RenderScope.Target, attachmentCount);
                        if (recordingState.RenderScope.AttachmentSignature is not null)
                            VkFrameBuffer.WriteFinalLayouts(recordingState.RenderScope.AttachmentSignature, finalLayouts);
                        else
                            vkFbo.WriteFinalLayouts(finalLayouts);
                    }
                }

                Api!.CmdEndRenderPass(recordingState.CommandBuffer);
                if (recordingState.RenderScope.Target is not null && recordingState.RenderScope.AttachmentSignature is not null)
                {
                    VkFrameBuffer? vkFbo = GenericToAPI<VkFrameBuffer>(recordingState.RenderScope.Target);
                    if (vkFbo is not null)
                    {
                        RecordFboAttachmentAccessState(
                            recordingState.CommandBuffer,
                            vkFbo,
                            recordingState.RenderScope.AttachmentSignature,
                            useReferenceLayouts: false);
                    }
                }
            }

            if (recordingState.RenderPassLabelActive)
            {
                CmdEndLabel(recordingState.CommandBuffer);
                recordingState.RenderPassLabelActive = false;
            }
            recordingState.RenderScope.Deactivate();

        }

        private void BeginDynamicRenderingScope(
            CommandBuffer commandBuffer,
            scoped in DynamicRenderingScopePlan plan,
            bool secondaryContents,
            bool preferKhrDynamicRendering)
        {
            ReadOnlySpan<DynamicRenderingAttachmentPlan> colorPlans = plan.ColorAttachments;
            RenderingAttachmentInfo* colorAttachments = stackalloc RenderingAttachmentInfo[Math.Max(colorPlans.Length, 1)];
            for (int i = 0; i < colorPlans.Length; i++)
                colorAttachments[i] = colorPlans[i].ToRenderingAttachmentInfo();

            RenderingAttachmentInfo depthAttachment = plan.HasDepthAttachment
                ? plan.DepthAttachment.ToRenderingAttachmentInfo()
                : default;
            RenderingAttachmentInfo stencilAttachment = plan.HasStencilAttachment
                ? plan.StencilAttachment.ToRenderingAttachmentInfo()
                : default;

            RenderingInfo renderingInfo = new()
            {
                SType = StructureType.RenderingInfo,
                Flags =
                    plan.InheritanceRenderingFlags |
                    (secondaryContents
                        ? RenderingFlags.ContentsSecondaryCommandBuffersBit
                        : 0),
                RenderArea = plan.RenderArea,
                ViewMask = plan.ViewMask,
                LayerCount = plan.LayerCount,
                ColorAttachmentCount = (uint)colorPlans.Length,
                PColorAttachments = colorPlans.Length > 0 ? colorAttachments : null,
                PDepthAttachment = plan.HasDepthAttachment ? &depthAttachment : null,
                PStencilAttachment = plan.HasStencilAttachment ? &stencilAttachment : null,
            };

            if (plan.LocalRead.Enabled && SupportsDynamicRenderingLocalRead)
            {
                DynamicRenderingLocalReadPlan localRead = plan.LocalRead;
                RenderingAttachmentLocationInfo localReadAttachmentLocations = default;
                RenderingInputAttachmentIndexInfo localReadInputIndices = default;
                uint* colorAttachmentLocations = stackalloc uint[Math.Max(colorPlans.Length, 1)];
                uint* colorInputAttachmentIndices = stackalloc uint[Math.Max(colorPlans.Length, 1)];
                uint* depthInputAttachmentIndex = stackalloc uint[1];
                uint* stencilInputAttachmentIndex = stackalloc uint[1];
                void* localReadPNext = renderingInfo.PNext;

                if (TryAppendDynamicRenderingLocalReadPNext(
                    in localRead,
                    (uint)colorPlans.Length,
                    ref localReadPNext,
                    &localReadAttachmentLocations,
                    &localReadInputIndices,
                    colorAttachmentLocations,
                    colorInputAttachmentIndices,
                    depthInputAttachmentIndex,
                    stencilInputAttachmentIndex))
                {
                    renderingInfo.PNext = localReadPNext;
                }
            }

            CmdBeginDynamicRendering(
                commandBuffer,
                &renderingInfo,
                preferKhrDynamicRendering);
        }

        private static SampleCountFlags ResolveDynamicRenderingSampleCount(FrameBufferAttachmentSignature[] signatures)
        {
            for (int i = 0; i < signatures.Length; i++)
            {
                if (signatures[i].Role == AttachmentRole.Color && signatures[i].Samples != default)
                    return signatures[i].Samples;
            }

            for (int i = 0; i < signatures.Length; i++)
            {
                if (signatures[i].Role != AttachmentRole.Resolve && signatures[i].Samples != default)
                    return signatures[i].Samples;
            }

            return SampleCountFlags.Count1Bit;
        }

        internal void BeginRenderPassForTarget(scoped ref PrimaryCommandBufferRecordingState recordingState, XRFrameBuffer? target, int passIndex, in FrameOpContext context, bool secondaryContents = false)
            => BeginRenderingForTarget(ref recordingState, target, passIndex, in context, secondaryContents);

        private void BeginRenderingForTarget(scoped ref PrimaryCommandBufferRecordingState recordingState, XRFrameBuffer? target, int passIndex, in FrameOpContext context, bool secondaryContents = false)
        {
            // Assumes no active render pass.
            if (target is null)
            {
                bool useDynamicRendering = recordingState.Policy.UseDynamicRendering &&
                    recordingState.SwapchainTarget.IsValid;

                CmdBeginLabel(recordingState.CommandBuffer, useDynamicRendering ? "Rendering:Swapchain" : "RenderPass:Swapchain");
                recordingState.RenderPassLabelActive = true;

                if (useDynamicRendering)
                {
                    // On the first frame for a given swapchain image, it starts in UNDEFINED.
                    // Re-entries within the same command buffer keep the image in color-attachment
                    // layout until the final close transitions it to PresentSrcKhr.
                    ImageLayout colorOldLayout = ResolveCurrentSwapchainColorLayout(ref recordingState);

                    // Preserve swapchain contents on re-entry so composited scene is not wiped.
                    bool overlaySwapchainPass = IsOverlayContext(context);
                    bool loadExistingSwapchainColor =
                        recordingState.SwapchainClearedThisFrame ||
                        recordingState.SwapchainWrittenOutsideRenderPass ||
                        (overlaySwapchainPass && recordingState.ImageWasEverPresentedAtRecordStart);
                    AttachmentLoadOp colorLoadOp = loadExistingSwapchainColor
                        ? AttachmentLoadOp.Load
                        : AttachmentLoadOp.Clear;

                    // Depth can always re-clear on re-entry; only the color contents
                    // (the composited scene) need to survive across render pass restarts.
                    AttachmentLoadOp depthLoadOp = AttachmentLoadOp.Clear;

                    ImageMemoryBarrier colorBarrier = new()
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = 0,
                        DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                        OldLayout = colorOldLayout,
                        NewLayout = ImageLayout.ColorAttachmentOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = recordingState.SwapchainTarget.Image,
                        SubresourceRange = new ImageSubresourceRange
                        {
                            AspectMask = ImageAspectFlags.ColorBit,
                            BaseMipLevel = 0,
                            LevelCount = 1,
                            BaseArrayLayer = 0,
                            LayerCount = 1
                        }
                    };

                    ImageSubresourceRange depthRange = new()
                    {
                        AspectMask = recordingState.SwapchainTarget.DepthAspect,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    };
                    bool hasRecordedDepthState = TryGetRecordedImageAccessState(
                        recordingState.CommandBuffer,
                        recordingState.SwapchainTarget.DepthImage,
                        depthRange,
                        out VulkanImageAccessState recordedDepthState);
                    ImageLayout depthOldLayout = hasRecordedDepthState
                        ? recordedDepthState.Layout
                        : ImageLayout.Undefined;

                    ImageMemoryBarrier depthBarrier = new()
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = hasRecordedDepthState
                            ? (AccessFlags)(ulong)recordedDepthState.AccessMask
                            : AccessFlags.None,
                        DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                        OldLayout = depthOldLayout,
                        NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = recordingState.SwapchainTarget.DepthImage,
                        SubresourceRange = depthRange
                    };

                    ImageMemoryBarrier* preRenderingBarriers = stackalloc ImageMemoryBarrier[2];
                    uint preRenderingBarrierCount = 0;
                    if (colorOldLayout != ImageLayout.ColorAttachmentOptimal)
                        preRenderingBarriers[preRenderingBarrierCount++] = colorBarrier;
                    preRenderingBarriers[preRenderingBarrierCount++] = depthBarrier;

                    CmdPipelineBarrierTracked(
                        recordingState.CommandBuffer,
                        PipelineStageFlags.ColorAttachmentOutputBit |
                            (hasRecordedDepthState
                                ? (PipelineStageFlags)(ulong)recordedDepthState.StageMask
                                : PipelineStageFlags.None),
                        PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        preRenderingBarrierCount,
                        preRenderingBarriers);

                    ClearValue* dynamicClearValues = stackalloc ClearValue[2];
                    WriteFrozenClearValues(dynamicClearValues, 2, in recordingState.ClearState);

                    Span<DynamicRenderingAttachmentPlan> colorAttachmentPlans = stackalloc DynamicRenderingAttachmentPlan[1];
                    colorAttachmentPlans[0] = new DynamicRenderingAttachmentPlan(
                        recordingState.SwapchainTarget.Image,
                        recordingState.SwapchainTarget.ImageView,
                        recordingState.SwapchainTarget.ImageFormat,
                        ImageAspectFlags.ColorBit,
                        colorOldLayout,
                        ImageLayout.ColorAttachmentOptimal,
                        ImageLayout.PresentSrcKhr,
                        colorLoadOp,
                        AttachmentStoreOp.Store,
                        dynamicClearValues[0]);

                    DynamicRenderingAttachmentPlan depthAttachmentPlan = new(
                        recordingState.SwapchainTarget.DepthImage,
                        recordingState.SwapchainTarget.DepthView,
                        recordingState.SwapchainTarget.DepthFormat,
                        recordingState.SwapchainTarget.DepthAspect,
                        depthOldLayout,
                        ImageLayout.DepthStencilAttachmentOptimal,
                        ImageLayout.DepthStencilAttachmentOptimal,
                        depthLoadOp,
                        AttachmentStoreOp.DontCare,
                        dynamicClearValues[1]);

                    DynamicRenderingFormatSignature swapchainDynamicRenderingFormats =
                        CreateSwapchainDynamicRenderingFormatSignature(recordingState.SwapchainTarget.ImageFormat, recordingState.SwapchainTarget.DepthFormat);
                    DynamicRenderingScopePlan scopePlan = new(
                        new Rect2D
                        {
                            Offset = new Offset2D(0, 0),
                            Extent = recordingState.SwapchainTarget.Extent
                        },
                        1u,
                        0u,
                        colorAttachmentPlans,
                        depthAttachmentPlan,
                        true,
                        default,
                        false,
                        false,
                        swapchainDynamicRenderingFormats,
                        SampleCountFlags.Count1Bit);

                    BeginDynamicRenderingScope(
                        recordingState.CommandBuffer,
                        in scopePlan,
                        secondaryContents,
                        recordingState.Policy.PreferKhrDynamicRendering);

                    recordingState.RenderScope.Activate(
                        null,
                        usesDynamicRendering: true,
                        default,
                        default,
                        scopePlan.FormatSignature,
                        null,
                        scopePlan.RenderArea,
                        depthStencilReadOnly: false,
                        scopePlan.LocalReadSignature,
                        scopePlan.InheritanceRenderingFlags);
                    recordingState.UsedSwapchainDynamicRendering = true;
                    recordingState.SwapchainInColorAttachmentLayout = true;
                    recordingState.SwapchainFinalLayout = ImageLayout.ColorAttachmentOptimal;
                    recordingState.SwapchainClearedThisFrame = true;
                    if (TargetTraceEnabled)
                    {
                        Debug.Vulkan(
                            "[VulkanTarget] begin target='<swapchain>' pass={0} passName='{1}' dynamic=true imageIndex={2} colorView=0x{3:X} depthView=0x{4:X} extent={5}x{6} load={7} secondary={8}",
                            passIndex,
                            ResolvePassName(context.PassMetadata, passIndex),
                            recordingState.ImageIndex,
                            recordingState.SwapchainTarget.ImageView.Handle,
                            recordingState.SwapchainTarget.DepthView.Handle,
                            recordingState.SwapchainTarget.Extent.Width,
                            recordingState.SwapchainTarget.Extent.Height,
                            colorLoadOp,
                            secondaryContents);
                    }
                    return;
                }

                // Fallback: traditional render pass path.
                // Use ResourceRuntime.SwapchainLoadRenderPass (LoadOp.Load) on re-entry to preserve contents.
                bool legacyOverlaySwapchainPass = IsOverlayContext(context);
                bool legacyLoadExistingSwapchainColor =
                    recordingState.SwapchainClearedThisFrame ||
                    recordingState.SwapchainWrittenOutsideRenderPass ||
                    (legacyOverlaySwapchainPass && recordingState.ImageWasEverPresentedAtRecordStart);
                AttachmentLoadOp legacySwapchainLoadOp = legacyLoadExistingSwapchainColor
                    ? AttachmentLoadOp.Load
                    : AttachmentLoadOp.Clear;
                RenderPass selectedRenderPass = legacyLoadExistingSwapchainColor
                    ? recordingState.SwapchainTarget.LoadRenderPass
                    : recordingState.SwapchainTarget.RenderPass;

                RenderPassBeginInfo renderPassInfo = new()
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = selectedRenderPass,
                    Framebuffer = recordingState.SwapchainTarget.Framebuffer,
                    RenderArea = new Rect2D
                    {
                        Offset = new Offset2D(0, 0),
                        Extent = recordingState.SwapchainRecordExtent
                    }
                };

                const uint attachmentCount = 2;
                ClearValue* clearValues = stackalloc ClearValue[(int)attachmentCount];
                WriteFrozenClearValues(clearValues, attachmentCount, in recordingState.ClearState);
                renderPassInfo.ClearValueCount = attachmentCount;
                renderPassInfo.PClearValues = clearValues;

                CmdBeginRenderPassTracked(
                    recordingState.CommandBuffer,
                    &renderPassInfo,
                    secondaryContents ? SubpassContents.SecondaryCommandBuffers : SubpassContents.Inline);

                recordingState.RenderScope.Activate(
                    null,
                    usesDynamicRendering: false,
                    selectedRenderPass,
                    recordingState.SwapchainTarget.Framebuffer,
                    default,
                    null,
                    renderPassInfo.RenderArea,
                    depthStencilReadOnly: false);
                recordingState.SwapchainClearedThisFrame = true;
                if (TargetTraceEnabled)
                {
                    Debug.Vulkan(
                        "[VulkanTarget] begin target='<swapchain>' pass={0} passName='{1}' dynamic=false imageIndex={2} renderPass=0x{3:X} framebuffer=0x{4:X} extent={5}x{6} load={7} secondary={8}",
                        passIndex,
                        ResolvePassName(context.PassMetadata, passIndex),
                        recordingState.ImageIndex,
                        selectedRenderPass.Handle,
                        recordingState.SwapchainTarget.Framebuffer.Handle,
                        recordingState.SwapchainRecordExtent.Width,
                        recordingState.SwapchainRecordExtent.Height,
                        legacySwapchainLoadOp,
                        secondaryContents);
                }
                return;
            }

            var vkFrameBuffer = GenericToAPI<VkFrameBuffer>(target) ?? throw new InvalidOperationException("Failed to resolve Vulkan framebuffer for target.");
            vkFrameBuffer.EnsureCurrent();

            string fboName = string.IsNullOrWhiteSpace(target.Name)
                ? $"FBO[{target.GetHashCode()}]"
                : target.Name!;
            if (CanRecordCommandBufferDebugLabels)
                recordingState.RenderPassLabelActive = CmdBeginLabel(recordingState.CommandBuffer, $"{(recordingState.Policy.UseDynamicRendering ? "Rendering" : "RenderPass")}:{fboName}");

            // Look up the CURRENT tracked layout of each attachment so the render
            // pass can use those as initialLayout (preserving content) instead of
            // Undefined (which discards content).
            //
            // We always query â€” not just when this FBO was previously bound this
            // frame â€” because attachments can be SHARED across framebuffers (e.g.
            // the deferred GBuffer and the forward pass share the depth/stencil
            // texture).  The forward pass deliberately does not clear depth and
            // relies on the GBuffer-written depth; if we only preserved content
            // for FBOs already seen this frame, the first forward-pass bind would
            // discard the GBuffer depth and every depth-tested draw (skybox,
            // forward meshes, gizmo) would fail.  Querying the per-image tracked
            // layout also accounts for barrier-planner transitions or blits that
            // changed the actual image layout since the last render pass ended.
            bool targetReenteredThisCommandBuffer = recordingState.FboLayoutTracking.ContainsKey(target);
            ImageLayout[]? trackedLayouts = QueryCurrentAttachmentLayouts(
                target,
                vkFrameBuffer,
                recordingState.CommandBuffer);
            // Update the tracking dict so that subsequent users see the
            // same layouts we resolved here.
            if (trackedLayouts is not null)
                recordingState.FboLayoutTracking[target] = trackedLayouts;
            FrameBufferAttachmentSignature[] fboSignature = vkFrameBuffer.ResolveAttachmentSignatureForPass(
                passIndex,
                context.PassMetadata,
                trackedLayouts,
                recordingState.RenderGraphPlan.CompiledGraph.Synchronization,
                preserveTrackedClearLoads: targetReenteredThisCommandBuffer);
            bool passDepthStencilReadOnly = VkFrameBuffer.UsesReadOnlyDepthStencil(fboSignature);
            if (DeferredLightingDiagnostics.Enabled && DeferredLightingDiagnostics.IsWatchedFrameBufferName(fboName))
            {
                Debug.VulkanEvery(
                    $"DeferredLighting.BeginFBO.{fboName}",
                    TimeSpan.FromSeconds(1),
                    "[DeferredLightingDiag][BeginFBO] name='{0}' pass={1} dynamic={2} trackedLayouts={3} signature={4}",
                    fboName,
                    passIndex,
                    recordingState.Policy.UseDynamicRendering,
                    trackedLayouts is not null ? string.Join(",", trackedLayouts) : "null",
                    FormatFboAttachmentSignature(fboSignature));
            }

            Extent2D logicalFboExtent = ResolveFrameBufferDrawExtent(target);
            uint fboRenderWidth = logicalFboExtent.Width;
            uint fboRenderHeight = logicalFboExtent.Height;
            if (vkFrameBuffer.FramebufferWidth > 0)
                fboRenderWidth = Math.Min(fboRenderWidth, vkFrameBuffer.FramebufferWidth);
            if (vkFrameBuffer.FramebufferHeight > 0)
                fboRenderHeight = Math.Min(fboRenderHeight, vkFrameBuffer.FramebufferHeight);
            Extent2D attachmentCompatibleExtent = vkFrameBuffer.ResolveAttachmentCompatibleDrawExtent();
            if (attachmentCompatibleExtent.Width > 0)
                fboRenderWidth = Math.Min(fboRenderWidth, attachmentCompatibleExtent.Width);
            if (attachmentCompatibleExtent.Height > 0)
                fboRenderHeight = Math.Min(fboRenderHeight, attachmentCompatibleExtent.Height);

            Rect2D fboRenderArea = new()
            {
                Offset = new Offset2D(0, 0),
                // Use the attachment-compatible extent. Dynamic rendering
                // validates against image-view dimensions, which can be
                // smaller than the FBO's base texture dimensions for
                // reduced-resolution passes or mip-level targets.
                Extent = new Extent2D(Math.Max(fboRenderWidth, 1u), Math.Max(fboRenderHeight, 1u))
            };

            if (recordingState.Policy.UseDynamicRendering)
            {
                if (CommandRecordingDiagnosticsEnabled)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.BeginRendering.FBO.{fboName}.{fboSignature.Length}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] BeginRendering FBO='{0}' pass={1} attachments={2} fbDims={3}x{4} trackedLayouts={5}",
                        fboName,
                        passIndex,
                        fboSignature.Length,
                        vkFrameBuffer.FramebufferWidth,
                        vkFrameBuffer.FramebufferHeight,
                        trackedLayouts is not null ? string.Join(",", trackedLayouts) : "null");
                }

                TransitionFboAttachmentsForDynamicRendering(
                    recordingState.CommandBuffer,
                    target,
                    fboSignature,
                    beginRendering: true);
                uint dynamicAttachmentCountFbo = Math.Max((uint)fboSignature.Length, 1u);
                ClearValue* dynamicClearValuesFbo = stackalloc ClearValue[(int)dynamicAttachmentCountFbo];
                VulkanCommandClearStateSnapshot clearState = recordingState.ClearState;
                ColorF4 clearColor = clearState.ClearColor;
                vkFrameBuffer.WriteClearValues(
                    dynamicClearValuesFbo,
                    dynamicAttachmentCountFbo,
                    fboSignature,
                    in clearColor,
                    clearState.ClearDepth,
                    clearState.ClearStencil);

                int colorAttachmentCount = 0;
                for (int i = 0; i < fboSignature.Length; i++)
                {
                    if (fboSignature[i].Role == AttachmentRole.Color)
                        colorAttachmentCount++;
                }

                Span<DynamicRenderingAttachmentPlan> colorAttachmentPlans = stackalloc DynamicRenderingAttachmentPlan[Math.Max(colorAttachmentCount, 1)];
                Span<uint> colorAttachmentSourceIndices = stackalloc uint[Math.Max(colorAttachmentCount, 1)];
                Span<DynamicRenderingAttachmentPlan> resolveAttachmentPlans = stackalloc DynamicRenderingAttachmentPlan[Math.Max(fboSignature.Length, 1)];
                Span<uint> resolveAttachmentSourceIndices = stackalloc uint[Math.Max(fboSignature.Length, 1)];
                int colorAttachmentIndex = 0;
                int resolveAttachmentCount = 0;
                DynamicRenderingAttachmentPlan depthAttachmentPlan = default;
                DynamicRenderingAttachmentPlan stencilAttachmentPlan = default;
                bool hasDepthAttachment = false;
                bool hasStencilAttachment = false;

                for (int i = 0; i < fboSignature.Length; i++)
                {
                    if (!vkFrameBuffer.TryGetAttachmentView(i, out ImageView view))
                        throw new InvalidOperationException($"Framebuffer '{fboName}' attachment {i} has no valid Vulkan image view.");

                    FrameBufferAttachmentSignature signature = fboSignature[i];
                    Image attachmentImage = default;
                    if (TryGetDescriptorHeapImageViewCreateInfo(view, out ImageViewCreateInfo attachmentViewInfo) &&
                        attachmentViewInfo.Image.Handle != 0)
                    {
                        attachmentImage = attachmentViewInfo.Image;
                    }
                    else if (vkFrameBuffer.TryGetAttachmentTarget(
                            i,
                            out IFrameBufferAttachement? attachmentTarget,
                            out _,
                            out _,
                            out _) &&
                        TryResolveFrameBufferAttachmentImage(
                            attachmentTarget,
                            out Image attachmentTargetImage))
                    {
                        attachmentImage = attachmentTargetImage;
                    }

                    DynamicRenderingAttachmentPlan attachmentPlan = new(
                        attachmentImage,
                        view,
                        signature.Format,
                        signature.AspectMask,
                        signature.InitialLayout,
                        signature.ReferenceLayout,
                        signature.FinalLayout,
                        signature.LoadOp,
                        signature.StoreOp,
                        dynamicClearValuesFbo[i]);

                    if (signature.Role == AttachmentRole.Color)
                    {
                        colorAttachmentPlans[colorAttachmentIndex] = attachmentPlan;
                        colorAttachmentSourceIndices[colorAttachmentIndex] = signature.ColorIndex;
                        colorAttachmentIndex++;
                        continue;
                    }

                    if (signature.Role == AttachmentRole.Resolve)
                    {
                        resolveAttachmentPlans[resolveAttachmentCount] = attachmentPlan;
                        resolveAttachmentSourceIndices[resolveAttachmentCount] = signature.ColorIndex;
                        resolveAttachmentCount++;
                        continue;
                    }

                    if (signature.Role is AttachmentRole.Depth or AttachmentRole.DepthStencil &&
                        (signature.AspectMask & ImageAspectFlags.DepthBit) != 0)
                    {
                        depthAttachmentPlan = attachmentPlan;
                        hasDepthAttachment = true;
                    }

                    if (signature.Role is AttachmentRole.Stencil or AttachmentRole.DepthStencil &&
                        (signature.AspectMask & ImageAspectFlags.StencilBit) != 0)
                    {
                        stencilAttachmentPlan = new DynamicRenderingAttachmentPlan(
                            attachmentImage,
                            view,
                            signature.Format,
                            signature.AspectMask,
                            signature.InitialLayout,
                            signature.ReferenceLayout,
                            signature.FinalLayout,
                            signature.StencilLoadOp,
                            signature.StencilStoreOp,
                            dynamicClearValuesFbo[i]);
                        hasStencilAttachment = true;
                    }
                }

                for (int resolveIndex = 0; resolveIndex < resolveAttachmentCount; resolveIndex++)
                {
                    uint sourceColorIndex = resolveAttachmentSourceIndices[resolveIndex];
                    int sourcePlanIndex = -1;
                    for (int colorIndex = 0; colorIndex < colorAttachmentCount; colorIndex++)
                    {
                        if (colorAttachmentSourceIndices[colorIndex] == sourceColorIndex)
                        {
                            sourcePlanIndex = colorIndex;
                            break;
                        }
                    }

                    if (sourcePlanIndex < 0)
                    {
                        throw new InvalidOperationException(
                            $"Framebuffer '{fboName}' has a resolve attachment for color {sourceColorIndex}, but the dynamic rendering scope has no matching color source.");
                    }

                    colorAttachmentPlans[sourcePlanIndex] = colorAttachmentPlans[sourcePlanIndex].WithResolve(
                        in resolveAttachmentPlans[resolveIndex],
                        ResolveModeFlags.AverageBit);
                }

                uint fboViewMask = vkFrameBuffer.MultiviewViewMask;
                uint fboLayerCount = VulkanDynamicRenderingUtilities.ResolveLayerCount(vkFrameBuffer.FramebufferLayers, fboViewMask);
                DynamicRenderingFormatSignature targetDynamicRenderingFormats = CreateDynamicRenderingFormatSignature(
                    fboSignature,
                    fboViewMask,
                    fboLayerCount);

                DynamicRenderingScopePlan scopePlan = new(
                    fboRenderArea,
                    fboLayerCount,
                    fboViewMask,
                    colorAttachmentPlans[..colorAttachmentCount],
                    depthAttachmentPlan,
                    hasDepthAttachment,
                    stencilAttachmentPlan,
                    hasStencilAttachment,
                    passDepthStencilReadOnly,
                    targetDynamicRenderingFormats,
                    ResolveDynamicRenderingSampleCount(fboSignature));

                if (XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw &&
                    string.Equals(fboName, "LightingAccumFBO", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(
                        "[DynamicRendering.Native] target='{0}' cb=0x{1:X} area={2}x{3} layers={4} viewMask=0x{5:X} colors={6} depth={7} stencil={8} samples={9}",
                        fboName,
                        recordingState.CommandBuffer.Handle,
                        scopePlan.RenderArea.Extent.Width,
                        scopePlan.RenderArea.Extent.Height,
                        scopePlan.LayerCount,
                        scopePlan.ViewMask,
                        scopePlan.FormatSignature.DescribeColorFormats(),
                        scopePlan.FormatSignature.DepthAttachmentFormat,
                        scopePlan.FormatSignature.StencilAttachmentFormat,
                        scopePlan.SampleCount);
                    for (int colorIndex = 0; colorIndex < colorAttachmentCount; colorIndex++)
                    {
                        DynamicRenderingAttachmentPlan colorPlan = colorAttachmentPlans[colorIndex];
                        Console.Error.WriteLine(
                            "[DynamicRendering.Native] color[{0}] image=0x{1:X} view=0x{2:X} format={3} aspect={4} initial={5} render={6} final={7} load={8} store={9} resolveView=0x{10:X} resolveMode={11} resolveLayout={12}",
                            colorIndex,
                            colorPlan.Image.Handle,
                            colorPlan.ImageView.Handle,
                            colorPlan.Format,
                            colorPlan.AspectMask,
                            colorPlan.InitialLayout,
                            colorPlan.RenderingLayout,
                            colorPlan.FinalLayout,
                            colorPlan.LoadOp,
                            colorPlan.StoreOp,
                            colorPlan.ResolveImageView.Handle,
                            colorPlan.ResolveMode,
                            colorPlan.ResolveImageLayout);
                    }
                    Console.Error.Flush();
                }

                BeginDynamicRenderingScope(
                    recordingState.CommandBuffer,
                    in scopePlan,
                    secondaryContents,
                    recordingState.Policy.PreferKhrDynamicRendering);

                recordingState.RenderScope.Activate(
                    target,
                    usesDynamicRendering: true,
                    default,
                    default,
                    scopePlan.FormatSignature,
                    fboSignature,
                    scopePlan.RenderArea,
                    passDepthStencilReadOnly,
                    scopePlan.LocalReadSignature,
                    scopePlan.InheritanceRenderingFlags);
                if (TargetTraceEnabled)
                {
                    Debug.Vulkan(
                        "[VulkanTarget] begin target='{0}' targetId={1} pass={2} passName='{3}' dynamic=true framebuffer=0x{4:X} attachments={5} extent={6}x{7} layers={8} viewMask=0x{9:X} formats={10} secondary={11}",
                        fboName,
                        target.GetHashCode(),
                        passIndex,
                        ResolvePassName(context.PassMetadata, passIndex),
                        vkFrameBuffer.FrameBuffer.Handle,
                        fboSignature.Length,
                        scopePlan.RenderArea.Extent.Width,
                        scopePlan.RenderArea.Extent.Height,
                        scopePlan.LayerCount,
                        scopePlan.ViewMask,
                        recordingState.RenderScope.DynamicRenderingFormats,
                        secondaryContents);
                }
                return;
            }

            // Keep the legacy fallback on the same explicit layout contract as
            // dynamic rendering. This removes the fragile dependency on cached
            // render-pass initial layouts when physical images are reused by a
            // newly compiled render graph.
            TransitionFboAttachmentsForDynamicRendering(
                recordingState.CommandBuffer,
                target,
                fboSignature,
                beginRendering: true);
            fboSignature = CreateLegacyRenderPassSignature(fboSignature);
            RenderPass passRenderPass = GetOrCreateFrameBufferRenderPass(fboSignature);

            if (VulkanFrameDiagnosticsTraceEnabled)
            {
                Debug.VulkanEvery(
                $"Vulkan.BeginRP.FBO.{fboName}.{passRenderPass.Handle:X}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] BeginRenderPassForTarget FBO='{0}' pass={1} renderPass=0x{2:X} attachments={3} fbDims={4}x{5} trackedLayouts={6}",
                fboName,
                passIndex,
                passRenderPass.Handle,
                vkFrameBuffer.AttachmentCount,
                vkFrameBuffer.FramebufferWidth,
                vkFrameBuffer.FramebufferHeight,
                trackedLayouts is not null ? string.Join(",", trackedLayouts) : "null");
            }
            RenderPassBeginInfo fboPassInfo = new()
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = passRenderPass,
                Framebuffer = vkFrameBuffer.FrameBuffer,
                RenderArea = fboRenderArea
            };

            uint attachmentCountFbo = Math.Max(vkFrameBuffer.AttachmentCount, 1u);
            ClearValue* clearValuesFbo = stackalloc ClearValue[(int)attachmentCountFbo];
            VulkanCommandClearStateSnapshot legacyClearState = recordingState.ClearState;
            ColorF4 legacyClearColor = legacyClearState.ClearColor;
            vkFrameBuffer.WriteClearValues(
                clearValuesFbo,
                attachmentCountFbo,
                fboSignature,
                in legacyClearColor,
                legacyClearState.ClearDepth,
                legacyClearState.ClearStencil);
            fboPassInfo.ClearValueCount = attachmentCountFbo;
            fboPassInfo.PClearValues = clearValuesFbo;

            CmdBeginRenderPassTracked(
                recordingState.CommandBuffer,
                &fboPassInfo,
                secondaryContents ? SubpassContents.SecondaryCommandBuffers : SubpassContents.Inline);
            RecordFboAttachmentAccessState(
                recordingState.CommandBuffer,
                vkFrameBuffer,
                fboSignature,
                useReferenceLayouts: true);

            recordingState.RenderScope.Activate(
                target,
                usesDynamicRendering: false,
                passRenderPass,
                vkFrameBuffer.FrameBuffer,
                default,
                fboSignature,
                fboPassInfo.RenderArea,
                passDepthStencilReadOnly);
            if (TargetTraceEnabled)
            {
                Debug.Vulkan(
                "[VulkanTarget] begin target='{0}' targetId={1} pass={2} passName='{3}' dynamic=false renderPass=0x{4:X} framebuffer=0x{5:X} attachments={6} extent={7}x{8} secondary={9} signature={10}",
                    fboName,
                    target.GetHashCode(),
                    passIndex,
                    ResolvePassName(context.PassMetadata, passIndex),
                    passRenderPass.Handle,
                    vkFrameBuffer.FrameBuffer.Handle,
                    vkFrameBuffer.AttachmentCount,
                    fboPassInfo.RenderArea.Extent.Width,
                    fboPassInfo.RenderArea.Extent.Height,
                secondaryContents,
                FormatFboAttachmentSignature(fboSignature));
            }
        }
    }
}
