using System;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Renders every hovered or selected primary scene mesh into an un-occluded
/// silhouette mask at output resolution so occluded silhouettes can be drawn dotted.
/// </summary>
[RenderCommandInfo(ERenderCommandCategory.Scene, DoesGpuWork = true)]
[RenderPipelineScriptCommand]
public sealed class VPRC_RenderEditorHighlightSilhouette : ViewportRenderCommand
{
    private const string SyntheticPassName = "EditorHighlightSilhouette";

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
        (int)EDefaultRenderPass.OnTopForward,
        (int)EDefaultRenderPass.PostBloomForward,
        (int)EDefaultRenderPass.PostMotionBlurForward,
        (int)EDefaultRenderPass.PostDepthOfFieldForward,
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

        if (ParentPipeline is not AdvancedRenderPipeline { EditorHighlightModule: not null })
            return false;

        XRRenderPipelineInstance? activeInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (activeInstance is null)
            return false;

        RenderCommandCollection commands = activeInstance.ActiveMeshRenderCommands;
        for (int i = 0; i < RenderPasses.Length; i++)
        {
            if (commands.HasHighlightedMeshCommands(RenderPasses[i]))
                return true;
        }

        return false;
    }

    protected override void Execute()
    {
        if (ParentPipeline is not AdvancedRenderPipeline advanced ||
            advanced.EditorHighlightModule is null ||
            RenderPasses.Length == 0)
        {
            return;
        }

        XRMaterial material = advanced.GetEditorHighlightSilhouetteMaterial();
        RenderCommandCollection commands = ActivePipelineInstance.ActiveMeshRenderCommands;
        if (commands is null)
            return;

        int renderGraphPass = ResolveRenderGraphPassIndex();
        material.RenderPass = renderGraphPass;

        var renderState = ActivePipelineInstance.RenderState;
        using var passScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(renderGraphPass);
        using var overrideTicket = renderState.PushOverrideMaterial(material);
        using var pipelineTicket = renderState.PushForceShaderPipelines();
        using var generatedVertexTicket = renderState.PushForceGeneratedVertexProgram();

        for (int i = 0; i < RenderPasses.Length; i++)
            commands.RenderHighlightedMeshCommands(RenderPasses[i], material);
    }

    private int ResolveRenderGraphPassIndex()
    {
        if (ParentPipeline?.PassMetadata is not { Count: > 0 } metadata)
            return (int)EDefaultRenderPass.OpaqueForward;

        foreach (RenderPassMetadata pass in metadata)
        {
            if (string.Equals(pass.Name, SyntheticPassName, StringComparison.OrdinalIgnoreCase))
                return pass.PassIndex;
        }

        return (int)EDefaultRenderPass.OpaqueForward;
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);

        var builder = context.GetOrCreateSyntheticPass(SyntheticPassName, ERenderGraphPassStage.Graphics);
        builder
            .UseEngineDescriptors()
            .UseMaterialDescriptors();

        if (context.CurrentRenderTarget is not { } target)
            return;

        builder.UseColorAttachment(
            MakeFboColorResource(target.Name),
            target.ColorAccess,
            target.ConsumeColorLoadOp(),
            target.GetColorStoreOp());
    }
}
