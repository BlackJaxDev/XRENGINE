using XREngine.Data.Rendering;
using XREngine.Rendering.GI.Contracts;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Composes a provider-produced HDR indirect-radiance texture into the host's
/// selected HDR target. Provider lifecycle ownership stays outside this command.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_GlobalIlluminationCompositePass : ViewportRenderCommand
{
    public string SourceQuadFBOName { get; set; } = string.Empty;
    public string DestinationFBOName { get; set; } = string.Empty;
    public string OutputTextureName { get; set; } = string.Empty;
    public string ProducerPassName { get; set; } = string.Empty;
    public bool ForceMonoWhenPipelineIsMono { get; set; } = true;

    private int _passIndex = int.MinValue;
    private string? _passName;

    private string PassName
        => _passName ??= $"GlobalIlluminationComposite.{OutputTextureName}.To.{DestinationFBOName}";

    protected override bool ShouldExecuteThisFrame()
        => GlobalIlluminationCompositionState.TryGetPrepared(ActivePipelineInstance, out _, out _, out bool shouldCompose) &&
            shouldCompose;

    protected override void Execute()
    {
        if (!GlobalIlluminationCompositionState.TryGetPrepared(
                ActivePipelineInstance, out bool replaceDestination, out _, out bool shouldCompose) ||
            !shouldCompose)
            return;

        XRQuadFrameBuffer? source = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(SourceQuadFBOName);
        XRFrameBuffer? destination = ActivePipelineInstance.GetFBO<XRFrameBuffer>(DestinationFBOName);
        if (source is null || destination is null)
            return;

        if (!TryResolvePassIndex())
            return;

        if (source.Material?.RenderOptions.BlendModeAllDrawBuffers is { } blend)
            blend.RgbDstFactor = replaceDestination ? EBlendingFactor.Zero : EBlendingFactor.One;

        bool stereo = ActivePipelineInstance.Pipeline is ISceneRenderPipelineFeatureProvider { Stereo: true };
        using (RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(_passIndex))
            source.Render(destination, forceNoStereo: ForceMonoWhenPipelineIsMono && !stereo);
        GlobalIlluminationCompositionState.MarkComposited(ActivePipelineInstance);
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        if (string.IsNullOrWhiteSpace(OutputTextureName) || string.IsNullOrWhiteSpace(DestinationFBOName))
            return;

        var draw = context.GetOrCreateSyntheticPass(PassName, ERenderGraphPassStage.Graphics);
        draw.UseEngineDescriptors();
        draw.UseMaterialDescriptors();
        if (!string.IsNullOrWhiteSpace(ProducerPassName))
            draw.DependsOn(context.GetOrCreateSyntheticPass(ProducerPassName, ERenderGraphPassStage.Compute).PassIndex);
        draw.SampleTexture(MakeTextureResource(OutputTextureName));
        draw.UseColorAttachment(MakeFboColorResource(DestinationFBOName), ERenderGraphAccess.ReadWrite,
            ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);
    }

    private bool TryResolvePassIndex()
    {
        if (_passIndex != int.MinValue)
            return true;
        if (ParentPipeline?.PassMetadata is { } metadata)
            foreach (RenderPassMetadata pass in metadata)
                if (pass.Name == PassName)
                {
                    _passIndex = pass.PassIndex;
                    return true;
                }

        Debug.RenderingWarningEvery("GI.Composite.MissingPassMetadata", TimeSpan.FromSeconds(2),
            "GI composition is waiting for render-graph pass metadata.");
        return false;
    }
}
