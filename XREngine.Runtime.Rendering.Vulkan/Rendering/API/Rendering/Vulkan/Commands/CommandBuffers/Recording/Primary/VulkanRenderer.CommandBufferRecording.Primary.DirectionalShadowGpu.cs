using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    /// <summary>One cascade and its immutable caster-policy inputs.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct AdvancedDirectionalShadowCullPushConstants(
        uint cascadeIndex,
        uint payloadCapacity,
        uint layerMask,
        uint zeroToOneDepth,
        in Matrix4x4 viewProjection)
    {
        public readonly uint CascadeIndex = cascadeIndex;
        public readonly uint PayloadCapacity = payloadCapacity;
        public readonly uint LayerMask = layerMask;
        public readonly uint ZeroToOneDepth = zeroToOneDepth;
        public readonly Matrix4x4 ViewProjection = viewProjection;
    }

    /// <summary>
    /// Culls full canonical inputs into private cascade streams. The CPU reads
    /// no visibility count and records only sealed indirect-count ranges.
    /// </summary>
    private unsafe int RecordAdvancedDirectionalShadowGpuPayload(
        scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedVisibilityOperationPayload payload,
        in VulkanPrimaryOperationRecordingInfo info,
        VulkanPreparedStableBinStream bins)
    {
        VulkanAdvancedDirectionalShadowResourceState shadow =
            payload.DirectionalShadowResources;
        VulkanAdvancedDirectionalShadowLaneStorage lane =
            payload.Request.DirectionalShadowLane!;
        if (!shadow.IsValid || shadow.FrameSlot != payload.State.FrameSlot ||
            shadow.FrameGeneration != payload.State.FrameGeneration ||
            shadow.OperationKey !=
                state.Ops.GetHeader(info.OperationIndex).PayloadIndex ||
            shadow.CascadeCount != (uint)lane.CascadeCount ||
            shadow.PayloadCount != payload.State.PayloadCapacity ||
            shadow.GroupCount != payload.State.IndexedInstanceGroupCount ||
            shadow.RangeCount != payload.State.RangeCapacity ||
            payload.DirectionalShadowCullProgram is not { IsLinked: true } cull ||
            payload.DirectionalShadowFinalizeProgram is not { IsLinked: true } finalize ||
            cull.LinkGeneration != payload.DirectionalShadowCullLinkGeneration ||
            finalize.LinkGeneration != payload.DirectionalShadowFinalizeLinkGeneration ||
            payload.DirectionalShadowCullPipeline.Handle == 0 ||
            payload.DirectionalShadowFinalizePipeline.Handle == 0)
        {
            throw new VulkanPlanPreconditionException(
                "Strict directional shadow compute state changed after frame-plan sealing.");
        }
        VulkanAdvancedVisibilityResourceState shadowVisibility = payload.State with
        {
            DescriptorSet = shadow.DescriptorSet,
            MeshPayloads = shadow.Members,
            EarlyIndexedGroupCounts = shadow.GroupCounts,
            RangeCounts = shadow.RangeCounts,
            IndirectArguments = shadow.IndexedArguments,
            Counters = shadow.Counters,
            ViewCount = shadow.CascadeCount,
            IndirectArgumentCapacity = checked(shadow.CascadeCount * shadow.PayloadCount),
        };
        if (!shadowVisibility.IsValid)
            throw new VulkanPlanPreconditionException(
                "Strict directional shadow stream offsets are invalid.");

        if (state.RenderScope.IsActive)
            EndActiveRenderPass(ref state);
        using var gpuScope = TryBeginVulkanGpuProfilerScope(
            state.CommandBuffer, DirectionalCascadeGpuProfilerPath);
        EmitMemoryBarrierMask(state.CommandBuffer, EMemoryBarrierMask.ShaderStorage);
        uint casterWorkgroups = DivideRoundUp(shadow.PayloadCount, 256u);
        uint groupWorkgroups = DivideRoundUp(shadow.GroupCount, 256u);
        bool zeroToOneDepth = RuntimeEngine.Rendering.EffectiveClipDepthRange ==
            ERenderClipDepthRange.ZeroToOne;
        ReadOnlySpan<Matrix4x4> matrices = lane.ViewProjections;
        for (uint cascade = 0u; cascade < shadow.CascadeCount; ++cascade)
        {
            BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Compute,
                payload.DirectionalShadowCullPipeline);
            BindAdvancedVisibilityDescriptorSets(state.CommandBuffer,
                PipelineBindPoint.Compute, cull.PipelineLayout,
                in payload, shadow.DescriptorSet);
            PushConstantsTracked(state.CommandBuffer, cull.PipelineLayout,
                VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(DeviceContext),
                0u, new AdvancedDirectionalShadowCullPushConstants(
                    cascade, shadow.PayloadCount, lane.CullingLayerMask,
                    zeroToOneDepth ? 1u : 0u, in matrices[(int)cascade]));
            Api!.CmdDispatch(state.CommandBuffer, casterWorkgroups, 1u, 1u);
            EmitMemoryBarrierMask(state.CommandBuffer, EMemoryBarrierMask.ShaderStorage);
            BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Compute,
                payload.DirectionalShadowFinalizePipeline);
            BindAdvancedVisibilityDescriptorSets(state.CommandBuffer,
                PipelineBindPoint.Compute, finalize.PipelineLayout,
                in payload, shadow.DescriptorSet);
            PushConstantsTracked(state.CommandBuffer, finalize.PipelineLayout,
                VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(DeviceContext),
                0u, new AdvancedVisibilityPreparationPushConstants(
                    cascade,
                    checked(cascade * shadow.PayloadCount),
                    shadow.PayloadCount,
                    checked(cascade * shadow.RangeCount)));
            Api.CmdDispatch(state.CommandBuffer, groupWorkgroups, 1u, 1u);
        }
        EmitAdvancedVisibilityRasterReadBarrier(ref state);

        BeginRenderPassForTarget(ref state, payload.Request.Target,
            info.PassIndex, state.ActiveContext);
        if (!state.RenderScope.IsActive ||
            state.RenderScope.Target != payload.TargetClosure.Target ||
            !state.RenderScope.UsesDynamicRendering ||
            state.RenderScope.DepthStencilReadOnly ||
            !state.RenderScope.DynamicRenderingFormats.Equals(
                payload.TargetClosure.DynamicRenderingFormats))
        {
            throw new VulkanPlanPreconditionException(
                "Strict directional shadow raster lost its sealed atlas page.");
        }

        CmdBeginLabel(state.CommandBuffer, "Advanced.DirectionalShadowGpuRaster");
        ReadOnlySpan<VulkanPreparedStableBinHeader> headers = bins.Headers;
        ReadOnlySpan<VulkanPreparedStableBinRecord> records = bins.Records;
        ReadOnlySpan<Viewport> viewports = lane.Viewports;
        ReadOnlySpan<Rect2D> scissors = lane.Scissors;
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
        for (uint cascade = 0u; cascade < shadow.CascadeCount; ++cascade)
        {
            Viewport viewport = viewports[(int)cascade];
            Rect2D scissor = scissors[(int)cascade];
            SetViewportScissorTracked(state.CommandBuffer, in viewport, in scissor);
            ClearRect tileRect = new()
            {
                Rect = scissor,
                BaseArrayLayer = 0u,
                LayerCount = 1u,
            };
            Api!.CmdClearAttachments(state.CommandBuffer, 1u, &tileClear,
                1u, &tileRect);
            for (int headerIndex = 0; headerIndex < headers.Length; ++headerIndex)
            {
                ref readonly VulkanPreparedStableBinHeader header =
                    ref headers[headerIndex];
                VulkanVisibilityRasterPipeline raster = header.ShadowRasterPipeline;
                if (!header.IsRasterReady || !raster.IsValid ||
                    raster.TargetClosure != payload.TargetClosure ||
                    raster.Program.LinkGeneration != raster.ProgramLinkGeneration ||
                    header.SubmissionPlan!.ResolvedStrategy !=
                        EMeshSubmissionStrategy.GpuIndirectZeroReadback ||
                    header.IndirectRange.Key.Producer !=
                        EAdvancedGeometryProducer.IndirectIndexed)
                {
                    throw new VulkanPlanPreconditionException(
                        "A strict directional shadow range changed after sealing.");
                }
                VulkanResidentDrawTemplateNativeState native = header.NativeState;
                if (native.PrimitiveCount != 1 ||
                    native.Primitive0.Topology != PrimitiveTopology.TriangleList ||
                    !native.Primitive0.Indexed || native.VertexBufferCount != 1 ||
                    native.GetVertexBinding(0) != 0u ||
                    native.Primitive0.IndexBuffer.Handle == 0)
                {
                    throw new VulkanPlanPreconditionException(
                        "A strict directional shadow range has no canonical index binding.");
                }
                AdvancedIndirectRange range = header.IndirectRange with
                {
                    FirstPayloadIndex = checked(header.IndirectRange.FirstPayloadIndex +
                        cascade * shadow.PayloadCount),
                    ArgumentBufferOffset = checked(header.IndirectRange.ArgumentBufferOffset +
                        cascade * shadow.PayloadCount * 20u),
                    CountBufferOffset = checked(header.IndirectRange.CountBufferOffset +
                        cascade * shadow.RangeCount * sizeof(uint)),
                };
                if (!VulkanStableBinSubmissionLowering.TryLower(
                        header.SubmissionPlan!, in header, in range,
                        in shadowVisibility, out VulkanStableBinSubmission submission,
                        out VulkanStableBinSubmissionLoweringFailure lowerFailure))
                {
                    throw new VulkanPlanPreconditionException(
                        $"A strict directional shadow range could not lower: {lowerFailure}.");
                }
                BindPipelineTracked(state.CommandBuffer,
                    PipelineBindPoint.Graphics, raster.Pipeline);
                BindAdvancedVisibilityDescriptorSets(state.CommandBuffer,
                    PipelineBindPoint.Graphics, raster.PipelineLayout,
                    in payload, shadow.DescriptorSet);
                VulkanVisibilityGeometryRecordClosure geometry =
                    records[header.RecordOffset].VisibilityGeometryClosure;
                BindVertexBufferTracked(state.CommandBuffer,
                    native.GetVertexBinding(0), native.GetVertexBuffer(0),
                    geometry.PreparedVertexSource.Offset);
                BindIndexBufferTracked(state.CommandBuffer,
                    native.Primitive0.IndexBuffer, geometry.IndexSlice.Offset,
                    native.Primitive0.IndexType);
                PushConstantsTracked(state.CommandBuffer, raster.PipelineLayout,
                    pushStages, 0u,
                    new AdvancedDirectionalShadowRasterPushConstants(
                        range.FirstPayloadIndex,
                        (uint)range.Key.Producer,
                        cascade, 1u, in matrices[(int)cascade]));
                if (!TryRecordStableBinSubmission(state.CommandBuffer,
                        in submission, null,
                        records.Slice(header.RecordOffset, header.RecordCount),
                        out VulkanStableBinSubmissionRecordingFailure recordFailure))
                {
                    throw new VulkanPlanPreconditionException(
                        $"Strict directional shadow indirect-count recording failed: {recordFailure}.");
                }
            }
        }
        CmdEndLabel(state.CommandBuffer);
        return info.OperationIndex;
    }
}
