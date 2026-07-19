using System;
using System.Collections.Generic;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Builds or refreshes the engine's current scene BVH 
/// and publishes the resulting buffers into pipeline variables.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_BuildAccelerationStructure : ViewportRenderCommand
{
    internal const string RenderGraphPassName = nameof(VPRC_BuildAccelerationStructure);
    private int _resolvedRenderGraphPassIndex = int.MinValue;

    public string ReadyVariableName { get; set; } = "AccelerationStructureReady";
    public string NodeCountVariableName { get; set; } = "AccelerationStructureNodeCount";
    public string PrimitiveCountVariableName { get; set; } = "AccelerationStructurePrimitiveCount";
    public string RootNodeIndexVariableName { get; set; } = "AccelerationStructureRootNodeIndex";
    public string NodeBufferVariableName { get; set; } = "AccelerationStructureNodes";
    public string RangeBufferVariableName { get; set; } = "AccelerationStructureRanges";
    public string MortonBufferVariableName { get; set; } = "AccelerationStructureMorton";
    /// <summary>
    /// Executes the command to build or refresh the acceleration structure (BVH) for the current scene. 
    /// It checks if the scene is available, 
    /// determines whether to use GPU BVH based on settings, 
    /// prepares the BVH for culling, 
    /// and publishes the relevant buffers and variables into the pipeline instance. 
    /// If the scene is null or there are no primitives, it publishes empty/default values.
    /// </summary>
    protected override void Execute()
    {
        // Get the current scene from the active pipeline instance's render state.
        // If the scene is null, publish empty/default values and return early.
        var scene = ActivePipelineInstance.RenderState.Scene;
        if (scene is null)
        {
            PublishEmpty();
            return;
        }

        // GPU-driven submission owns scene culling on the GPU and therefore uses the command BVH
        // whenever the active backend profile supports it. CpuDirect uses the CPU scene BVH instead.
        GPUScene gpuScene = scene.GPUCommands;
        EMeshSubmissionStrategy strategy = ResolveEffectiveMeshSubmissionStrategy();
        bool useGpuBvh = VulkanFeatureProfile.ResolveGpuBvhUsage(strategy);
        gpuScene.UseGpuBvh = useGpuBvh;
        gpuScene.UseInternalBvh = useGpuBvh;

        // If GPU BVH is not enabled, we can skip building the BVH and publish empty/default values.
        if (!useGpuBvh)
        {
            PublishEmpty();
            return;
        }

        // If there are no primitives in the scene, we can skip building the BVH and publish empty/default values.
        uint primitiveCount = gpuScene.TotalCommandCount;
        if (primitiveCount == 0)
        {
            PublishEmpty();
            return;
        }

        int passIndex = ResolveRenderGraphPassIndex();
        if (passIndex == int.MinValue && AbstractRenderer.Current is VulkanRenderer)
        {
            Debug.RenderingWarningEvery(
                "BuildAccelerationStructure.MissingRenderGraphPass",
                TimeSpan.FromSeconds(1),
                "[GPU BVH] Skipping acceleration-structure compute because no render-graph pass metadata was generated for '{0}'.",
                RenderGraphPassName);
            PublishEmpty();
            return;
        }

        // Vulkan compute submissions must carry the same pass identity that
        // DescribeRenderPass registered. OpenGL ignores the scope, but using it
        // there too keeps backend execution order identical when metadata exists.
        using IDisposable? passScope = passIndex == int.MinValue
            ? null
            : RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex);

        // Path A: when enabled, push skinned-mesh world-space AABBs straight into the
        // command-AABB buffer (BVH leaf bounds) via the reduce shader before we build
        // the BVH. Bypasses the CPU 8-corner transform for these slots.
        if (RuntimeEngine.Rendering.Settings.CalculateSkinnedBoundsInComputeShader
            && (RuntimeEngine.Rendering.Settings.SkinnedBoundsGpuDirectAabbWrite ||
                strategy.IsGpuZeroReadbackStrategy()))
            SkinnedMeshBoundsCalculator.Instance.RefreshAllSkinnedAabbs(gpuScene);

        // Prepare the BVH for culling, which may involve building or refreshing the BVH structure on the GPU.
        gpuScene.PrepareBvhForCulling(primitiveCount);

        var provider = (IGpuBvhProvider)gpuScene;
        var variables = ActivePipelineInstance.Variables;
        bool ready = provider.IsBvhReady;

        // Publish the relevant variables and buffers into the pipeline instance.
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

    private static EMeshSubmissionStrategy ResolveEffectiveMeshSubmissionStrategy()
    {
        XRViewport? viewport = RuntimeEngine.Rendering.State.RenderingPipelineState?.WindowViewport
            ?? RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState.WindowViewport
            ?? RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.LastWindowViewport;

        return viewport?.MeshSubmissionStrategyOverride
            ?? RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy();
    }

    private int ResolveRenderGraphPassIndex()
    {
        if (_resolvedRenderGraphPassIndex != int.MinValue)
            return _resolvedRenderGraphPassIndex;

        if (TryResolveRenderGraphPassIndex(ParentPipeline?.PassMetadata, out int passIndex))
            return _resolvedRenderGraphPassIndex = passIndex;

        if (TryResolveRenderGraphPassIndex(
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline?.PassMetadata,
            out passIndex))
            return _resolvedRenderGraphPassIndex = passIndex;

        return int.MinValue;
    }

    internal static bool TryResolveRenderGraphPassIndex(
        IReadOnlyCollection<RenderPassMetadata>? metadata,
        out int passIndex)
    {
        passIndex = int.MinValue;
        if (metadata is null)
            return false;

        foreach (RenderPassMetadata pass in metadata)
        {
            if (!string.Equals(pass.Name, RenderGraphPassName, StringComparison.OrdinalIgnoreCase))
                continue;

            passIndex = pass.PassIndex;
            return true;
        }

        return false;
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);

        // Describe the buffers that will be read/written by this pass in the render graph.
        var builder = context.GetOrCreateSyntheticPass(RenderGraphPassName, ERenderGraphPassStage.Compute);
        builder.ReadWriteBuffer(NodeBufferVariableName);
        builder.ReadWriteBuffer(RangeBufferVariableName);
        builder.ReadWriteBuffer(MortonBufferVariableName);
    }

    /// <summary>
    /// Publishes empty/default values for the acceleration structure variables in the pipeline instance.
    /// </summary>
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
