using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanPipelineVariantManifest
{
    /// <summary>
    /// Captures attachment compatibility without acquired image handles or dynamic
    /// viewport rectangles. The allocation signature retains resource format and
    /// sample-count changes; native buffer publication does not change it.
    /// </summary>
    internal static ulong ComputeTargetCompatibilitySignature(
        ulong resourceAllocationSignature,
        in SwapchainRecordingTarget target,
        bool dynamicRendering,
        RenderPass fallbackRenderPass)
    {
        var hash = new VulkanStableHash64(1u);
        hash.Add(resourceAllocationSignature);
        hash.Add(target.IsValid);
        hash.Add((int)target.ImageFormat);
        hash.Add((int)target.DepthFormat);
        if (!dynamicRendering || !target.IsValid)
        {
            hash.Add(target.RenderPass.Handle);
            hash.Add(target.LoadRenderPass.Handle);
            hash.Add(fallbackRenderPass.Handle);
        }
        return hash.Value;
    }

    /// <summary>
    /// Keeps every frozen planner context while excluding native barrier epochs.
    /// FramePlan.RenderGraphPlanSignature still validates those exact epochs at
    /// command recording; publishing a buffer must not restart pipeline warmup.
    /// </summary>
    internal static ulong ComputePlanCompatibilitySignature(
        VulkanCompiledRenderGraphPlan plan,
        FramePlan? framePlan,
        ulong targetCompatibilitySignature)
    {
        var hash = new VulkanStableHash64(1u);
        hash.Add(plan.CompatibilityIdentity);
        hash.Add(targetCompatibilitySignature);
        if (framePlan is null)
            return hash.Value;

        ReadOnlySpan<VulkanFrameOpPlannerStateKey> keys =
            framePlan.StaticPlannerContextKeys;
        ReadOnlySpan<VulkanRenderGraphPlan> plans =
            framePlan.StaticPlannerContextPlans;
        hash.Add(keys.Length);
        for (int index = 0; index < keys.Length; index++)
        {
            ref readonly VulkanFrameOpPlannerStateKey key = ref keys[index];
            hash.Add((int)key.ContextKind);
            hash.Add(key.PipelineIdentity);
            hash.Add(key.ViewportIdentity);
            hash.Add(key.LogicalViewId);
            hash.Add(key.ResourceGeneration);
            hash.Add(plans[index].CompatibilityIdentity);
        }
        return hash.Value;
    }

    /// <summary>
    /// Hashes only graphics requirements, including their exact operation indices.
    /// Matrices, descriptor contents, indirect buffer handles, and dynamic viewport
    /// rectangles belong to recording compatibility, not pipeline admission.
    /// </summary>
    internal static ulong ComputeDemandSignature(
        VulkanCompiledRenderGraphPlan plan,
        FrameOperationSequence ops,
        bool dynamicRendering,
        ulong planCompatibilitySignature,
        FramePlan? framePlan)
    {
        var hash = new VulkanStableHash64(1u);
        for (int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            ref readonly FrameOperationHeader header = ref ops.GetHeader(opIndex);
            if (header.OpCode is not (EVulkanPrimaryPlanNodeKind.MeshDraw or
                EVulkanPrimaryPlanNodeKind.IndirectDraw or
                EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount))
                continue;

            ref readonly FrameOpContext context = ref ops.GetContext(opIndex);
            VulkanCompiledRenderGraphPlan operationPlan =
                ResolveOperationPlan(plan, framePlan, in context);
            hash.Add(opIndex);
            hash.Add((int)header.OpCode);
            hash.Add((int)context.ContextKind);
            hash.Add(context.PipelineIdentity);
            hash.Add(context.ViewportIdentity);
            hash.Add(context.LogicalViewId);
            hash.Add(context.ResourceGeneration);
            hash.Add(context.OutputFrameBufferIdentity);
            hash.Add(context.OutputTargetIdentity);
            hash.Add(ComputePreparationSignature(
                ops, opIndex, operationPlan, dynamicRendering,
                planCompatibilitySignature));
        }
        return hash.Value;
    }

    private static VulkanCompiledRenderGraphPlan ResolveOperationPlan(
        VulkanCompiledRenderGraphPlan plan,
        FramePlan? framePlan,
        in FrameOpContext context)
        => framePlan is not null && framePlan.TryResolveRenderGraphPlan(
            in context, out VulkanRenderGraphPlan operationPlan)
                ? operationPlan.CompiledGraph.Plan
                : plan;

    private static ulong ComputePreparationSignature(
        FrameOperationSequence ops,
        int opIndex,
        VulkanCompiledRenderGraphPlan operationPlan,
        bool dynamicRendering,
        ulong planCompatibilitySignature)
    {
        ref readonly FrameOperationHeader header = ref ops.GetHeader(opIndex);
        ref readonly FrameOpContext context = ref ops.GetContext(opIndex);
        var hash = new VulkanStableHash64(2u);
        hash.Add(planCompatibilitySignature);
        hash.Add(operationPlan.CompatibilityIdentity);
        hash.Add(header.PassIndex);
        hash.Add(header.TargetIdentity);
        hash.Add(context.StereoEnabled);
        hash.Add(context.MultiviewEnabled);
        hash.Add(dynamicRendering);

        if (header.OpCode == EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount)
        {
            ref readonly MeshTaskDispatchIndirectCountPayload meshTask =
                ref ops.GetMeshTask(opIndex);
            hash.Add(meshTask.Program.BindingId);
            hash.Add(meshTask.ProgramLinkGeneration);
            hash.Add(meshTask.ProducerSnapshot.Target?.GetHashCode() ?? 0);
            hash.Add(meshTask.ProducerSnapshot.FixedFunctionState.GetHashCode());
            hash.Add(meshTask.ProducerSnapshot.IndexedViewportScissors.Count);
            return hash.Value;
        }

        PendingMeshDraw draw = header.OpCode == EVulkanPrimaryPlanNodeKind.MeshDraw
            ? ops.GetMeshDraw(opIndex).Draw
            : ops.GetIndirectDraw(opIndex).Draw;
        hash.Add(draw.PreparationCompatibilitySignature);
        hash.Add(draw.Renderer.BindingId);
        hash.Add(draw.PreparedProgramIdentity);
        hash.Add(draw.PreparedProgram?.BindingId ?? 0u);
        hash.Add(draw.PreparedProgramLinkGeneration);
        hash.Add((int)draw.RasterizationSamples);
        hash.Add(draw.DepthTestEnabled);
        hash.Add(draw.DepthWriteEnabled);
        hash.Add((int)draw.DepthCompareOp);
        hash.Add(draw.StencilTestEnabled);
        AddStencilState(ref hash, draw.FrontStencilState);
        AddStencilState(ref hash, draw.BackStencilState);
        hash.Add(draw.StencilWriteMask);
        hash.Add((int)draw.ColorWriteMask);
        hash.Add((int)draw.CullMode);
        hash.Add((int)draw.FrontFace);
        hash.Add(draw.BlendEnabled);
        hash.Add(draw.AlphaToCoverageEnabled);
        hash.Add((int)draw.ColorBlendOp);
        hash.Add((int)draw.AlphaBlendOp);
        hash.Add((int)draw.SrcColorBlendFactor);
        hash.Add((int)draw.DstColorBlendFactor);
        hash.Add((int)draw.SrcAlphaBlendFactor);
        hash.Add((int)draw.DstAlphaBlendFactor);
        hash.Add(draw.ViewportScissorCount);
        hash.Add(draw.MaterialOverride is not null);
        hash.Add(draw.IsStereoPass);
        return hash.Value;
    }

    private static void AddStencilState(
        ref VulkanStableHash64 hash,
        StencilOpState state)
    {
        hash.Add((int)state.FailOp);
        hash.Add((int)state.PassOp);
        hash.Add((int)state.DepthFailOp);
        hash.Add((int)state.CompareOp);
        hash.Add(state.CompareMask);
        hash.Add(state.WriteMask);
        hash.Add(state.Reference);
    }
}
