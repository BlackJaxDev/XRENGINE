using System.Numerics;
using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    private int RecordAdvancedNativeComputePayload(
        scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedVisibilityOperationPayload payload,
        in VulkanPrimaryOperationRecordingInfo info)
    {
        if (payload.NativeComputeClosure is not { IsValid: true } closure ||
            !payload.NativeComputePipelines.IsCurrent || payload.NativeComputeDescriptorSet.Handle == 0 ||
            !payload.State.IsValid || !payload.SceneState.IsValid)
            throw new VulkanPlanPreconditionException("Advanced native compute reached recording without its immutable resources and pipeline generation.");
        if (state.RenderScope.IsActive) EndActiveRenderPass(ref state);

        uint width = closure.Identity.ResolvedExtent.Width, height = closure.Identity.ResolvedExtent.Height;
        uint tilesX = DivideRoundUp(width, 16), tilesY = DivideRoundUp(height, 16);
        uint totalTiles = checked(tilesX * tilesY * payload.State.ViewCount);
        uint depthSlices = checked((uint)(closure.FroxelGrid.NativeSize / 16UL / totalTiles));
        if (depthSlices == 0 || closure.ActiveTiles.NativeSize < checked(totalTiles * 16UL) ||
            closure.KernelCounts.NativeSize < AdvancedRenderPipeline.DefaultMaxShadingKernels * 4UL ||
            closure.DispatchArguments.NativeSize < AdvancedRenderPipeline.DefaultMaxShadingKernels * 16UL)
            throw new VulkanPlanPreconditionException("Advanced native output capacity does not match the admitted extent and view count.");
        var clear = RuntimeEngine.StartupPresentationClearColor;
        VulkanAdvancedNativeShadingPushConstants push = new(width, height, tilesX, tilesY,
            closure.ViewIndex, payload.State.ViewCount, 0,
            (payload.Request.RequireNativeOutput ? 2u : 0u) |
            (payload.Request.EnableBuiltInAmbientOcclusion ? 4u : 0u) |
            (((uint)payload.Request.ShadingDebugView & 0xFFu) << 8), depthSlices,
            checked((uint)(closure.LightIndices.NativeSize / sizeof(uint))),
            payload.SceneState.Lights.Length / 128u,
            checked((uint)(closure.KernelTiles.NativeSize / 16u)),
            new Vector4(clear.R, clear.G, clear.B, clear.A));

        TransitionNativeInput(state.CommandBuffer, closure.Identity, closure.ViewIndex);
        TransitionNativeInput(state.CommandBuffer, closure.Metadata, closure.ViewIndex);
        if (payload.Request.Stage == EAdvancedRenderStage.WorkClassification)
        {
            FillNativeCounters(state.CommandBuffer, closure.ClassificationCounters);
            FillNativeCounters(state.CommandBuffer, closure.KernelCounts);
            RecordNativeDispatch(state.CommandBuffer, in payload, payload.NativeComputePipelines.Classify,
                in push, tilesX, tilesY, 1, "Advanced.ClassifyTiles");
            EmitNativeBufferDependency(state.CommandBuffer, closure.KernelCounts,
                AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit,
                PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit);
            EmitNativeBufferDependency(state.CommandBuffer, closure.ClassificationCounters,
                AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit,
                PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit);
            RecordNativeDispatch(state.CommandBuffer, in payload, payload.NativeComputePipelines.BuildArguments,
                in push, 1, 1, 1, "Advanced.BuildClassificationIndirect");
            EmitNativeBufferDependency(state.CommandBuffer, closure.DispatchArguments,
                AccessFlags.ShaderWriteBit, AccessFlags.IndirectCommandReadBit,
                PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.DrawIndirectBit);
            return info.OperationIndex;
        }

        TransitionNativeInput(state.CommandBuffer, closure.Depth, closure.ViewIndex);
        if (payload.Request.Stage == EAdvancedRenderStage.AmbientOcclusion)
        {
            TransitionNativeOutput(state.CommandBuffer, closure.AmbientOcclusion, closure.ViewIndex);
            RecordNativeDispatch(state.CommandBuffer, in payload, payload.NativeComputePipelines.AmbientOcclusion,
                in push, tilesX, tilesY, 1, "Advanced.GTAO");
            EmitMemoryBarrierMask(state.CommandBuffer, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
            return info.OperationIndex;
        }

        TransitionNativeInput(state.CommandBuffer, closure.AmbientOcclusion, closure.ViewIndex);
        TransitionNativeOutput(state.CommandBuffer, closure.Hdr, closure.ViewIndex);
        TransitionNativeOutput(state.CommandBuffer, closure.Velocity, closure.ViewIndex);
        TransitionNativeOutput(state.CommandBuffer, closure.Reactive, closure.ViewIndex);
        TransitionNativeOutput(state.CommandBuffer, closure.ShadingDiagnostics, closure.ViewIndex);
        FillNativeCounters(state.CommandBuffer, closure.LightingCounters);
        RecordNativeDispatch(state.CommandBuffer, in payload, payload.NativeComputePipelines.BuildFroxels,
            in push, DivideRoundUp(tilesX, 8), DivideRoundUp(tilesY, 8), DivideRoundUp(depthSlices, 4), "Advanced.BuildFroxels");
        EmitNativeBufferDependency(state.CommandBuffer, closure.FroxelGrid,
            AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit,
            PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit);
        EmitNativeBufferDependency(state.CommandBuffer, closure.LightIndices,
            AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit,
            PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit);
        RecordNativeDispatch(state.CommandBuffer, in payload, payload.NativeComputePipelines.Background,
            in push, tilesX, tilesY, 1, "Advanced.Background");
        EmitMemoryBarrierMask(state.CommandBuffer, EMemoryBarrierMask.ShaderImageAccess);

        VulkanAdvancedComputePipeline shade = payload.NativeComputePipelines.Shade;
        CmdBeginLabel(state.CommandBuffer, "Advanced.NativeOpaque.Indirect");
        BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Compute, shade.Pipeline);
        BindAdvancedVisibilityDescriptorSets(state.CommandBuffer, PipelineBindPoint.Compute,
            shade.Program.PipelineLayout, in payload, payload.NativeComputeDescriptorSet);
        for (uint kernel = 0; kernel < AdvancedRenderPipeline.DefaultMaxShadingKernels; ++kernel)
        {
            push = push with { KernelIndex = kernel };
            PushNativeConstants(state.CommandBuffer, shade, in push);
            Api.CmdDispatchIndirect(state.CommandBuffer, closure.DispatchArguments.NativeBuffer,
                closure.DispatchArguments.NativeOffset + kernel * 16UL);
        }
        CmdEndLabel(state.CommandBuffer);
        EmitMemoryBarrierMask(state.CommandBuffer, EMemoryBarrierMask.ShaderImageAccess);
        push = push with { Flags = push.Flags | 1u };
        RecordNativeDispatch(state.CommandBuffer, in payload, shade,
            in push, tilesX, tilesY, 1, "Advanced.NativeOpaque.GpuOverflowRepair");
        EmitMemoryBarrierMask(state.CommandBuffer, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
        return info.OperationIndex;
    }

    private void RecordNativeDispatch(CommandBuffer commandBuffer,
        in VulkanAdvancedVisibilityOperationPayload payload, in VulkanAdvancedComputePipeline pipeline,
        in VulkanAdvancedNativeShadingPushConstants push, uint x, uint y, uint z, string label)
    {
        CmdBeginLabel(commandBuffer, label);
        BindPipelineTracked(commandBuffer, PipelineBindPoint.Compute, pipeline.Pipeline);
        BindAdvancedVisibilityDescriptorSets(commandBuffer, PipelineBindPoint.Compute,
            pipeline.Program.PipelineLayout, in payload, payload.NativeComputeDescriptorSet);
        PushNativeConstants(commandBuffer, pipeline, in push);
        Api.CmdDispatch(commandBuffer, x, y, z);
        CmdEndLabel(commandBuffer);
    }

    private void PushNativeConstants(CommandBuffer commandBuffer,
        in VulkanAdvancedComputePipeline pipeline, in VulkanAdvancedNativeShadingPushConstants push)
        => PushConstantsTracked(commandBuffer, pipeline.Program.PipelineLayout,
            VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(DeviceContext), 0, push);

    private void TransitionNativeInput(CommandBuffer commandBuffer, VulkanPhysicalImageGroup group, uint view)
        => EmitAdvancedVisibilityImageBarrier(commandBuffer, group, 0, view,
            ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, PipelineStageFlags.ComputeShaderBit, false);

    private void TransitionNativeOutput(CommandBuffer commandBuffer, VulkanPhysicalImageGroup group, uint view)
        => EmitAdvancedVisibilityImageBarrier(commandBuffer, group, 0, view,
            ImageLayout.General, AccessFlags.ShaderWriteBit, PipelineStageFlags.ComputeShaderBit, true);

    private void FillNativeCounters(CommandBuffer commandBuffer, in VulkanFrozenBufferBarrier buffer)
    {
        EmitNativeBufferDependency(commandBuffer, in buffer,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit, AccessFlags.TransferWriteBit,
            PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit);
        Api.CmdFillBuffer(commandBuffer, buffer.NativeBuffer, buffer.NativeOffset, buffer.NativeSize, 0);
        EmitNativeBufferDependency(commandBuffer, in buffer, AccessFlags.TransferWriteBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ComputeShaderBit);
    }

    private unsafe void EmitNativeBufferDependency(CommandBuffer commandBuffer,
        in VulkanFrozenBufferBarrier buffer, AccessFlags source, AccessFlags destination,
        PipelineStageFlags sourceStage, PipelineStageFlags destinationStage)
    {
        BufferMemoryBarrier barrier = new()
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = source, DstAccessMask = destination,
            SrcQueueFamilyIndex = uint.MaxValue, DstQueueFamilyIndex = uint.MaxValue,
            Buffer = buffer.NativeBuffer, Offset = buffer.NativeOffset, Size = buffer.NativeSize,
        };
        CmdPipelineBarrierTracked(commandBuffer, sourceStage, destinationStage,
            DependencyFlags.None, 0, null, 1, &barrier, 0, null);
    }
}
