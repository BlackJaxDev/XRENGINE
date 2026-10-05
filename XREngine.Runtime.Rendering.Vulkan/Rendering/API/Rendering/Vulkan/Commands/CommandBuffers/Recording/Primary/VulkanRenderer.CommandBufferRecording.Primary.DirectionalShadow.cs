using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    private static readonly string[] DirectionalCascadeGpuProfilerPath = ["Advanced", "Shadows", "DirectionalCascades"];

    /// <summary>
    /// Push block of the directional shadow lane programs: the canonical raster
    /// header followed by the cascade's row-vector world-to-clip matrix.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct AdvancedDirectionalShadowRasterPushConstants(
        uint meshArgumentBase,
        uint producerAndOrigin,
        uint cascadeIndex,
        uint flags,
        in Matrix4x4 viewProjection)
    {
        public readonly uint MeshArgumentBase = meshArgumentBase;
        public readonly uint ProducerAndOrigin = producerAndOrigin;
        public readonly uint CascadeIndex = cascadeIndex;
        public readonly uint Flags = flags;
        public readonly Matrix4x4 ViewProjection = viewProjection;
    }

    /// <summary>
    /// Records one deferred directional cascade group: the family's sealed
    /// canonical bins are drawn once per cascade into the atlas page with a
    /// depth-only pipeline, each cascade clearing and rasterizing only its tile.
    /// Casters come from the per-record masks that family preparation derived
    /// from the publication's cast-shadow flags and candidate bounds, so the
    /// recorded draw set matches the generic layered pass.
    /// </summary>
    private unsafe int RecordAdvancedDirectionalShadowRasterPayload(
        scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedVisibilityOperationPayload payload,
        in VulkanPrimaryOperationRecordingInfo info)
    {
        FramePlan framePlan = state.FramePlan
            ?? throw new VulkanPlanPreconditionException(
                "Advanced directional shadow raster reached recording without an accepted frame plan.");
        VulkanAdvancedDirectionalShadowLaneStorage lane = payload.Request.DirectionalShadowLane
            ?? throw new VulkanPlanPreconditionException(
                "Advanced directional shadow raster reached recording without its cascade group.");
        VulkanPreparedStableBinStream bins = framePlan.GetAdvancedVisibilityFamilyBins(
            payload.Request.Reservation, allowCreate: false);
        if (!payload.State.IsValid || !payload.SceneState.IsValid ||
            !payload.TargetClosure.IsValid ||
            payload.TargetClosure.Kind != EVulkanAdvancedVisibilityTargetKind.DirectionalShadow ||
            !bins.HasSealedSubmissionPlans || lane.CascadeCount <= 0 ||
            lane.RenderFrameId != payload.Request.RenderFrameId ||
            !ReferenceEquals(lane.Target, payload.Request.Target) ||
            lane.RecordMaskCount != bins.RecordCount || !info.BeginsRendering)
        {
            throw new VulkanPlanPreconditionException(
                "Advanced directional shadow raster reached recording without its sealed cascade group, target and stable-bin closure.");
        }
        if (ResourceRuntime.BackendObjects.Get(payload.Request.Target) is not
                VkFrameBuffer targetWrapper)
        {
            throw new VulkanPlanPreconditionException(
                "Advanced directional shadow raster lost its authoritative Vulkan framebuffer wrapper.");
        }
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
                $"Advanced directional shadow atlas page changed after sealing: {mismatch}.");
        }

        if (state.RenderScope.IsActive)
            EndActiveRenderPass(ref state);
        // The surrounding recorder owns the later render-scope closure.
        using var gpuScope = TryBeginVulkanGpuProfilerScope(state.CommandBuffer, DirectionalCascadeGpuProfilerPath);
        // Deformation and payload writes must be visible to the vertex stage
        // before the first cascade draw, exactly as for the visibility raster.
        EmitAdvancedVisibilityRasterReadBarrier(ref state);
        // The page is loaded, never cleared as a whole: other lights' tiles stay
        // resident and each cascade clears only its own tile below.
        BeginRenderPassForTarget(
            ref state,
            payload.Request.Target,
            info.PassIndex,
            state.ActiveContext);
        if (!state.RenderScope.IsActive ||
            state.RenderScope.Target != payload.TargetClosure.Target ||
            !state.RenderScope.UsesDynamicRendering ||
            state.RenderScope.DepthStencilReadOnly ||
            !state.RenderScope.DynamicRenderingFormats.Equals(
                payload.TargetClosure.DynamicRenderingFormats))
        {
            throw new VulkanPlanPreconditionException(
                "The active directional shadow render scope does not match its sealed atlas-page closure.");
        }

        CmdBeginLabel(state.CommandBuffer, "Advanced.DirectionalShadowRaster");
        ReadOnlySpan<VulkanPreparedStableBinHeader> headers = bins.Headers;
        ReadOnlySpan<VulkanPreparedStableBinRecord> allRecords = bins.Records;
        ReadOnlySpan<byte> masks = lane.RecordMasks;
        ReadOnlySpan<Viewport> viewports = lane.Viewports;
        ReadOnlySpan<Rect2D> scissors = lane.Scissors;
        ReadOnlySpan<Matrix4x4> viewProjections = lane.ViewProjections;
        ShaderStageFlags pushStages =
            VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(DeviceContext);
        ClearAttachment tileClear = new()
        {
            AspectMask = ImageAspectFlags.DepthBit,
            ClearValue = new ClearValue
            {
                DepthStencil = new ClearDepthStencilValue
                {
                    Depth = lane.DepthClearValue,
                    Stencil = 0u,
                },
            },
        };
        for (int cascade = 0; cascade < lane.CascadeCount; cascade++)
        {
            byte cascadeBit = (byte)(1 << cascade);
            Viewport viewport = viewports[cascade];
            Rect2D scissor = scissors[cascade];
            SetViewportScissorTracked(state.CommandBuffer, in viewport, in scissor);
            ClearRect tileRect = new()
            {
                Rect = scissor,
                BaseArrayLayer = 0u,
                LayerCount = 1u,
            };
            Api!.CmdClearAttachments(state.CommandBuffer, 1u, &tileClear, 1u, &tileRect);

            for (int headerIndex = 0; headerIndex < headers.Length; headerIndex++)
            {
                ref readonly VulkanPreparedStableBinHeader header = ref headers[headerIndex];
                if (!header.IsRasterReady)
                {
                    throw new VulkanPlanPreconditionException(
                        "A non-empty visibility bin has no sealed raster pipeline and submission plan.");
                }

                VulkanVisibilityRasterPipeline shadow = header.ShadowRasterPipeline;
                if (!shadow.IsValid || shadow.TargetClosure != payload.TargetClosure ||
                    shadow.Program.LinkGeneration != shadow.ProgramLinkGeneration)
                {
                    throw new VulkanPlanPreconditionException(
                        "A directional shadow pipeline closure changed after frame-plan sealing.");
                }
                VulkanSealedBinSubmissionPlan plan = header.SubmissionPlan!;
                if (plan.ResolvedStrategy != EMeshSubmissionStrategy.CpuDirect)
                {
                    throw new VulkanPlanPreconditionException(
                        "The directional shadow lane reached recording with a non-CPU-direct bin.");
                }

                // Skip the bind when no record of this bin reaches the cascade.
                int firstRecord = -1;
                int recordEnd = header.RecordOffset + header.RecordCount;
                for (int recordIndex = header.RecordOffset; recordIndex < recordEnd; recordIndex++)
                {
                    if ((masks[recordIndex] & cascadeBit) != 0)
                    {
                        firstRecord = recordIndex;
                        break;
                    }
                }
                if (firstRecord < 0)
                    continue;

                VulkanResidentDrawTemplateNativeState native = header.NativeState;
                if (native.PrimitiveCount != 1 ||
                    native.Primitive0.Topology != PrimitiveTopology.TriangleList ||
                    !native.Primitive0.Indexed || native.VertexBufferCount != 1 ||
                    native.GetVertexBinding(0) != 0u ||
                    native.Primitive0.IndexBuffer.Handle == 0)
                {
                    throw new VulkanPlanPreconditionException(
                        "A directional shadow bin has an invalid canonical packed-geometry closure.");
                }

                using (VulkanCpuStageScope bindingStage = new(
                           _frameTelemetry,
                           EVulkanCpuStage.PrimaryAdvancedRasterBindingOperation))
                {
                    BindPipelineTracked(
                        state.CommandBuffer,
                        PipelineBindPoint.Graphics,
                        shadow.Pipeline);
                    BindAdvancedVisibilityDescriptorSets(
                        state.CommandBuffer,
                        PipelineBindPoint.Graphics,
                        shadow.PipelineLayout,
                        in payload);
                    VulkanVisibilityGeometryRecordClosure geometryClosure =
                        allRecords[header.RecordOffset].VisibilityGeometryClosure;
                    BindVertexBufferTracked(
                        state.CommandBuffer,
                        native.GetVertexBinding(0),
                        native.GetVertexBuffer(0),
                        geometryClosure.PreparedVertexSource.Offset);
                    BindIndexBufferTracked(
                        state.CommandBuffer,
                        native.Primitive0.IndexBuffer,
                        geometryClosure.IndexSlice.Offset,
                        native.Primitive0.IndexType);
                    PushConstantsTracked(
                        state.CommandBuffer,
                        shadow.PipelineLayout,
                        pushStages,
                        0u,
                        new AdvancedDirectionalShadowRasterPushConstants(
                            header.IndirectRange.FirstPayloadIndex,
                            (uint)header.IndirectRange.Key.Producer,
                            (uint)cascade,
                            1u,
                            in viewProjections[cascade]));
                }

                using VulkanCpuStageScope drawStage = new(
                    _frameTelemetry,
                    EVulkanCpuStage.PrimaryAdvancedCpuDirectDrawOperation);
                for (int recordIndex = firstRecord; recordIndex < recordEnd; recordIndex++)
                {
                    if ((masks[recordIndex] & cascadeBit) == 0)
                        continue;

                    VulkanPreparedVisibilityDirectDraw draw =
                        allRecords[recordIndex].VisibilityDirectDraw;
                    if (!draw.IsValid)
                    {
                        throw new VulkanPlanPreconditionException(
                            "A directional shadow bin contains invalid frozen indexed arguments.");
                    }
                    Api!.CmdDrawIndexed(
                        state.CommandBuffer,
                        draw.IndexCount,
                        draw.InstanceCount,
                        draw.FirstIndex,
                        draw.VertexOffset,
                        draw.FirstInstance);
                }
            }
        }
        CmdEndLabel(state.CommandBuffer);
        return info.OperationIndex;
    }
}
