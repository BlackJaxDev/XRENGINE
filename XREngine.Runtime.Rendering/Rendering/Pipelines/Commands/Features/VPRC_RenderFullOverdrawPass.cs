using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

[RenderPipelineScriptCommand]
public sealed class VPRC_RenderFullOverdrawPass : ViewportRenderCommand
{
    private const string PassNamePrefix = "FullOverdraw";

    private static readonly int[] DefaultRenderPasses =
    [
        (int)EDefaultRenderPass.OpaqueDeferred,
        (int)EDefaultRenderPass.DeferredDecals,
        (int)EDefaultRenderPass.OpaqueForward,
        (int)EDefaultRenderPass.MaskedForward,
        (int)EDefaultRenderPass.WeightedBlendedOitForward,
        (int)EDefaultRenderPass.PerPixelLinkedListForward,
        (int)EDefaultRenderPass.DepthPeelingForward,
        (int)EDefaultRenderPass.TransparentForward,
    ];

    public int[] RenderPasses { get; set; } = DefaultRenderPasses;

    protected override bool ShouldExecuteThisFrame()
    {
        if (RuntimeEngine.Rendering.State.IsSceneCapturePass ||
            RuntimeEngine.Rendering.State.IsLightProbePass ||
            RuntimeEngine.Rendering.State.IsShadowPass ||
            RenderPasses.Length == 0)
        {
            return false;
        }

        XRRenderPipelineInstance? activeInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (activeInstance is null)
            return false;

        XRCamera? camera = activeInstance.RenderState.SceneCamera
            ?? activeInstance.RenderState.RenderingCamera
            ?? activeInstance.LastSceneCamera
            ?? activeInstance.LastRenderingCamera;

        if (!GpuBvhDebugSettings.TryResolve(camera, out GpuBvhDebugSettings? settings) ||
            settings?.FullOverdrawEnabled != true)
        {
            return false;
        }

        for (int i = 0; i < RenderPasses.Length; i++)
        {
            if (activeInstance.ActiveMeshRenderCommands.HasRenderingMeshCommands(RenderPasses[i]))
                return true;
        }

        return false;
    }

    protected override void Execute()
    {
        XRMaterial? material = ParentPipeline switch
        {
            DefaultRenderPipeline pipeline => pipeline.GetFullOverdrawCountMaterial(),
            DefaultRenderPipeline2 pipeline => pipeline.GetFullOverdrawCountMaterial(),
            _ => null,
        };

        if (material is null || RenderPasses.Length == 0)
            return;

        var commands = ActivePipelineInstance.ActiveMeshRenderCommands;
        if (commands is null)
            return;

        var renderState = ActivePipelineInstance.RenderState;
        using var overrideTicket = renderState.PushOverrideMaterial(material);
        using var pipelineTicket = renderState.PushForceShaderPipelines();
        using var generatedVertexTicket = renderState.PushForceGeneratedVertexProgram();

        EMeshSubmissionStrategy overdrawStrategy = ResolveOverrideSubmissionStrategy(
            RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy());
        bool useGpuRenderPath = overdrawStrategy != EMeshSubmissionStrategy.CpuDirect;

        for (int i = 0; i < RenderPasses.Length; i++)
        {
            int pass = RenderPasses[i];
            material.RenderPass = pass;
            int renderGraphPass = ResolveRenderGraphPassIndex(pass);

            using var passScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(renderGraphPass);
            if (useGpuRenderPath)
            {
                commands.RenderCPUFiltered(
                    pass,
                    static command => command is IRenderCommandMesh mesh && IsGpuPathCpuFallbackMesh(mesh),
                    respectCpuQueryOcclusion: true);

                commands.RenderGPU(pass, overdrawStrategy);
            }
            else
            {
                commands.RenderCPUFiltered(
                    pass,
                    static command => command is IRenderCommandMesh,
                    respectCpuQueryOcclusion: true);
            }
        }
    }

    private int ResolveRenderGraphPassIndex(int renderPass)
    {
        string passName = BuildRenderGraphPassName(renderPass);
        if (ParentPipeline?.PassMetadata is not { Count: > 0 } metadata)
            return renderPass;

        foreach (RenderPassMetadata pass in metadata)
        {
            if (string.Equals(pass.Name, passName, StringComparison.OrdinalIgnoreCase))
                return pass.PassIndex;
        }

        Debug.RenderingWarningEvery(
            $"FullOverdraw.MissingRenderGraphPass.{passName}",
            TimeSpan.FromSeconds(2),
            "[RenderDiag] Full-overdraw pass '{0}' has no matching render-graph metadata; using source pass {1}.",
            passName,
            FormatRenderPassName(renderPass));
        return renderPass;
    }

    private static bool IsGpuPathCpuFallbackMesh(IRenderCommandMesh meshCommand)
    {
        XRMaterial? material = meshCommand.MaterialOverride ?? meshCommand.Mesh?.Material;
        return meshCommand.ForceCpuRendering || material?.RenderOptions?.ExcludeFromGpuIndirect == true;
    }

    private static EMeshSubmissionStrategy ResolveOverrideSubmissionStrategy(EMeshSubmissionStrategy strategy)
    {
        if (!strategy.IsAnyMeshletStrategy())
            return strategy;

        return strategy == EMeshSubmissionStrategy.GpuMeshletInstrumented
            ? EMeshSubmissionStrategy.GpuIndirectInstrumented
            : EMeshSubmissionStrategy.GpuIndirectZeroReadback;
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);

        for (int i = 0; i < RenderPasses.Length; i++)
        {
            int renderPass = RenderPasses[i];
            string passName = BuildRenderGraphPassName(renderPass);
            var builder = context.GetOrCreateSyntheticPass(passName, ERenderGraphPassStage.Graphics);
            builder
                .UseEngineDescriptors()
                .UseMaterialDescriptors();

            if (context.CurrentRenderTarget is not { } target)
                continue;

            builder.UseColorAttachment(
                MakeFboColorResource(target.Name),
                target.ColorAccess,
                target.ConsumeColorLoadOp(),
                target.GetColorStoreOp());
        }
    }

    private static string BuildRenderGraphPassName(int renderPass)
        => $"{PassNamePrefix}_{FormatRenderPassName(renderPass)}";

    private static string FormatRenderPassName(int renderPass)
        => Enum.IsDefined(typeof(EDefaultRenderPass), renderPass)
            ? ((EDefaultRenderPass)renderPass).ToString()
            : renderPass.ToString();
}
