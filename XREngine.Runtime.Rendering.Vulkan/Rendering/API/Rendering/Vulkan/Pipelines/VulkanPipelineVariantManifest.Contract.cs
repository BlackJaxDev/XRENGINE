using System.Collections.ObjectModel;
using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable pipeline-variant demand derived from a compiled graph plan and its
/// prepared frame operations. External image handles and frame-local matrices are excluded.
/// </summary>
internal sealed class VulkanPipelineVariantManifest
{
    private readonly ReadOnlyCollection<VulkanPipelineVariantRequirement> _requirements;
    private int _warmupCompleted;

    private VulkanPipelineVariantManifest(
        ulong compatibilityIdentity,
        VulkanPipelineVariantRequirement[] requirements)
    {
        CompatibilityIdentity = compatibilityIdentity;
        _requirements = Array.AsReadOnly(requirements);
    }

    public ulong CompatibilityIdentity { get; }
    public ReadOnlyCollection<VulkanPipelineVariantRequirement> Requirements => _requirements;
    public bool WarmupCompleted => Volatile.Read(ref _warmupCompleted) != 0;

    public void MarkWarmupCompleted() => Volatile.Write(ref _warmupCompleted, 1);

    internal static VulkanPipelineVariantManifest Build(
        VulkanCompiledRenderGraphPlan plan,
        FrameOperationSequence ops,
        EMeshSubmissionStrategy submissionStrategy,
        bool dynamicRendering,
        ulong recordingStructuralSignature,
        ulong renderGraphPlanSignature,
        FramePlan? framePlan)
    {
        int requirementCount = 0;
        for (int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            if (ops.GetHeader(opIndex).OpCode is EVulkanPrimaryPlanNodeKind.MeshDraw or
                EVulkanPrimaryPlanNodeKind.IndirectDraw or
                EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount)
                requirementCount++;
        }

        var requirements = new VulkanPipelineVariantRequirement[requirementCount];
        var hash = new VulkanStableHash64(1u);
        hash.Add(renderGraphPlanSignature);
        hash.Add(recordingStructuralSignature);
        hash.Add((int)submissionStrategy);
        hash.Add(dynamicRendering);

        int requirementIndex = 0;
        for (int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            ref readonly FrameOperationHeader header = ref ops.GetHeader(opIndex);
            if (header.OpCode is not (EVulkanPrimaryPlanNodeKind.MeshDraw or
                EVulkanPrimaryPlanNodeKind.IndirectDraw or
                EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount))
                continue;

            if (header.OpCode == EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount)
            {
                ref readonly MeshTaskDispatchIndirectCountPayload meshTask =
                    ref ops.GetMeshTask(opIndex);
                int meshTaskPassIndex = header.PassIndex;
                FrameOpContext meshTaskContext = ops.GetContext(opIndex);
                VulkanCompiledRenderGraphPlan meshTaskPlan = plan;
                if (framePlan is not null &&
                    framePlan.TryResolveRenderGraphPlan(
                        in meshTaskContext,
                        out VulkanRenderGraphPlan meshTaskRenderGraphPlan))
                {
                    meshTaskPlan = meshTaskRenderGraphPlan.CompiledGraph.Plan;
                }

                RenderGraphPlanPass? meshTaskPlanPass = FindPass(
                    meshTaskPlan.Passes,
                    meshTaskPassIndex);
                string meshTaskPassName = meshTaskPlanPass?.Name ??
                    $"Pass{meshTaskPassIndex}";
                var meshTaskPreparationHash = new VulkanStableHash64(
                    schemaVersion: 1);
                meshTaskPreparationHash.Add(renderGraphPlanSignature);
                meshTaskPreparationHash.Add(meshTaskPlan.CompatibilityIdentity);
                meshTaskPreparationHash.Add(meshTask.Program.BindingId);
                meshTaskPreparationHash.Add(meshTask.ProgramLinkGeneration);
                meshTaskPreparationHash.Add(meshTask.ProducerSnapshot.Target?.GetHashCode() ?? 0);
                meshTaskPreparationHash.Add(meshTask.ProducerSnapshot.FixedFunctionState.GetHashCode());
                meshTaskPreparationHash.Add(meshTask.ProducerSnapshot.IndexedViewportScissors.Count);
                meshTaskPreparationHash.Add(dynamicRendering);

                requirements[requirementIndex++] = new VulkanPipelineVariantRequirement(
                    opIndex,
                    meshTaskPassIndex,
                    meshTaskPassName,
                    Required: meshTaskPlanPass?.RequiresPipelineReady ?? true,
                    submissionStrategy,
                    Shadow: meshTaskPassName.Contains("Shadow", StringComparison.OrdinalIgnoreCase),
                    Velocity: meshTaskPassName.Contains("Velocity", StringComparison.OrdinalIgnoreCase) || meshTaskPassName.Contains("Motion", StringComparison.OrdinalIgnoreCase),
                    EditorId: meshTaskPassName.Contains("Editor", StringComparison.OrdinalIgnoreCase) || meshTaskPassName.Contains("Picking", StringComparison.OrdinalIgnoreCase) || meshTaskPassName.Contains("TransformId", StringComparison.OrdinalIgnoreCase),
                    MaterialOverride: false,
                    Stereo: meshTaskContext.StereoEnabled,
                    Multiview: meshTaskContext.MultiviewEnabled,
                    DynamicRendering: dynamicRendering,
                    LegacyRenderPass: !dynamicRendering,
                    meshTaskPreparationHash.Value);

                hash.Add(opIndex);
                hash.Add(meshTaskPassIndex);
                hash.Add(meshTask.Program.BindingId);
                hash.Add(meshTask.ProgramLinkGeneration);
                hash.Add(meshTaskPlanPass?.RequiresPipelineReady ?? true);
                continue;
            }

            PendingMeshDraw draw = header.OpCode switch
            {
                EVulkanPrimaryPlanNodeKind.MeshDraw => ops.GetMeshDraw(opIndex).Draw,
                EVulkanPrimaryPlanNodeKind.IndirectDraw => ops.GetIndirectDraw(opIndex).Draw,
                _ => default,
            };
            int passIndex = header.PassIndex;
            FrameOpContext operationContext = ops.GetContext(opIndex);
            VulkanCompiledRenderGraphPlan operationPlan = plan;
            if (framePlan is not null &&
                framePlan.TryResolveRenderGraphPlan(
                    in operationContext,
                    out VulkanRenderGraphPlan renderGraphPlan))
            {
                operationPlan = renderGraphPlan.CompiledGraph.Plan;
            }
            RenderGraphPlanPass? planPass = FindPass(operationPlan.Passes, passIndex);
            string passName = planPass?.Name ?? $"Pass{passIndex}";
            bool shadow = passName.Contains("Shadow", StringComparison.OrdinalIgnoreCase);
            bool velocity = passName.Contains("Velocity", StringComparison.OrdinalIgnoreCase) ||
                passName.Contains("Motion", StringComparison.OrdinalIgnoreCase);
            bool editor = passName.Contains("Editor", StringComparison.OrdinalIgnoreCase) ||
                passName.Contains("Picking", StringComparison.OrdinalIgnoreCase) ||
                passName.Contains("TransformId", StringComparison.OrdinalIgnoreCase);
            bool materialOverride = draw.MaterialOverride is not null;
            bool stereo = draw.IsStereoPass || operationContext.StereoEnabled;
            bool multiview = operationContext.MultiviewEnabled;

            var preparationHash = new VulkanStableHash64(schemaVersion: 1);
            preparationHash.Add(renderGraphPlanSignature);
            preparationHash.Add(operationPlan.CompatibilityIdentity);
            preparationHash.Add(draw.PreparationCompatibilitySignature);
            preparationHash.Add(draw.Renderer.BindingId);
            preparationHash.Add(passIndex);
            preparationHash.Add(header.TargetIdentity);
            preparationHash.Add(draw.PreparedProgramIdentity);
            preparationHash.Add(draw.PreparedProgram?.BindingId ?? 0u);
            preparationHash.Add(draw.PreparedProgramLinkGeneration);
            preparationHash.Add((int)draw.RasterizationSamples);
            preparationHash.Add(draw.DepthTestEnabled);
            preparationHash.Add(draw.DepthWriteEnabled);
            preparationHash.Add((int)draw.DepthCompareOp);
            preparationHash.Add(draw.StencilTestEnabled);
            AddStencilState(ref preparationHash, draw.FrontStencilState);
            AddStencilState(ref preparationHash, draw.BackStencilState);
            preparationHash.Add(draw.StencilWriteMask);
            preparationHash.Add((int)draw.ColorWriteMask);
            preparationHash.Add((int)draw.CullMode);
            preparationHash.Add((int)draw.FrontFace);
            preparationHash.Add(draw.BlendEnabled);
            preparationHash.Add(draw.AlphaToCoverageEnabled);
            preparationHash.Add((int)draw.ColorBlendOp);
            preparationHash.Add((int)draw.AlphaBlendOp);
            preparationHash.Add((int)draw.SrcColorBlendFactor);
            preparationHash.Add((int)draw.DstColorBlendFactor);
            preparationHash.Add((int)draw.SrcAlphaBlendFactor);
            preparationHash.Add((int)draw.DstAlphaBlendFactor);
            preparationHash.Add(draw.ViewportScissorCount);
            preparationHash.Add(materialOverride);
            preparationHash.Add(stereo);
            preparationHash.Add(multiview);
            preparationHash.Add(dynamicRendering);

            requirements[requirementIndex++] = new VulkanPipelineVariantRequirement(
                opIndex,
                passIndex,
                passName,
                Required: planPass?.RequiresPipelineReady ?? true,
                submissionStrategy,
                shadow,
                velocity,
                editor,
                materialOverride,
                stereo,
                multiview,
                dynamicRendering,
                LegacyRenderPass: !dynamicRendering,
                preparationHash.Value);

            hash.Add(opIndex);
            hash.Add(passIndex);
            hash.Add(draw.PreparedProgramIdentity);
            hash.Add(draw.PreparedProgram?.BindingId ?? 0u);
            hash.Add(planPass?.RequiresPipelineReady ?? true);
            hash.Add(materialOverride);
            hash.Add(stereo);
            hash.Add(multiview);
        }

        return new VulkanPipelineVariantManifest(hash.Value, requirements);

        static void AddStencilState(
            ref VulkanStableHash64 hash,
            Silk.NET.Vulkan.StencilOpState state)
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

    private static RenderGraphPlanPass? FindPass(
        ReadOnlyCollection<RenderGraphPlanPass> passes,
        int passIndex)
    {
        for (int index = 0; index < passes.Count; index++)
        {
            if (passes[index].PassIndex == passIndex)
                return passes[index];
        }

        return null;
    }
}
