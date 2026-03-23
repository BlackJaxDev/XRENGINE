using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.RenderGraph;
using XREngine.Scene;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Builds or refreshes the engine's current scene BVH and publishes the resulting buffers into pipeline variables.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_BuildAccelerationStructure : ViewportRenderCommand
{
    public string ReadyVariableName { get; set; } = "AccelerationStructureReady";
    public string NodeCountVariableName { get; set; } = "AccelerationStructureNodeCount";
    public string PrimitiveCountVariableName { get; set; } = "AccelerationStructurePrimitiveCount";
    public string RootNodeIndexVariableName { get; set; } = "AccelerationStructureRootNodeIndex";
    public string NodeBufferVariableName { get; set; } = "AccelerationStructureNodes";
    public string RangeBufferVariableName { get; set; } = "AccelerationStructureRanges";
    public string MortonBufferVariableName { get; set; } = "AccelerationStructureMorton";
    public bool EnableGpuBvh { get; set; } = true;
    public bool EnableInternalSceneBvh { get; set; } = true;

    protected override void Execute()
    {
        var scene = ActivePipelineInstance.RenderState.Scene;
        if (scene is null)
        {
            PublishEmpty();
            return;
        }

        GPUScene gpuScene = scene.GPUCommands;
        if (EnableGpuBvh)
            gpuScene.UseGpuBvh = true;
        if (EnableInternalSceneBvh)
            gpuScene.UseInternalBvh = true;

        uint primitiveCount = gpuScene.TotalCommandCount;
        if (primitiveCount == 0)
        {
            PublishEmpty();
            return;
        }

        gpuScene.PrepareBvhForCulling(primitiveCount);

        var provider = (IGpuBvhProvider)gpuScene;
        var variables = ActivePipelineInstance.Variables;
        bool ready = provider.IsBvhReady;

        variables.Set(ReadyVariableName, ready);
        variables.Set(NodeCountVariableName, provider.BvhNodeCount);
        variables.Set(PrimitiveCountVariableName, primitiveCount);
        variables.Set(RootNodeIndexVariableName, 0u);

        if (ready && provider.BvhNodeBuffer is not null)
            variables.SetBuffer(NodeBufferVariableName, provider.BvhNodeBuffer);
        else
            variables.Remove(NodeBufferVariableName);

        if (ready && provider.BvhRangeBuffer is not null)
            variables.SetBuffer(RangeBufferVariableName, provider.BvhRangeBuffer);
        else
            variables.Remove(RangeBufferVariableName);

        if (ready && provider.BvhMortonBuffer is not null)
            variables.SetBuffer(MortonBufferVariableName, provider.BvhMortonBuffer);
        else
            variables.Remove(MortonBufferVariableName);
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);

        var builder = context.GetOrCreateSyntheticPass(nameof(VPRC_BuildAccelerationStructure), ERenderGraphPassStage.Compute);
        builder.ReadWriteBuffer(NodeBufferVariableName);
        builder.ReadWriteBuffer(RangeBufferVariableName);
        builder.ReadWriteBuffer(MortonBufferVariableName);
    }

    private void PublishEmpty()
    {
        var variables = ActivePipelineInstance.Variables;
        variables.Set(ReadyVariableName, false);
        variables.Set(NodeCountVariableName, 0u);
        variables.Set(PrimitiveCountVariableName, 0u);
        variables.Set(RootNodeIndexVariableName, 0u);
        variables.Remove(NodeBufferVariableName);
        variables.Remove(RangeBufferVariableName);
        variables.Remove(MortonBufferVariableName);
    }
}
