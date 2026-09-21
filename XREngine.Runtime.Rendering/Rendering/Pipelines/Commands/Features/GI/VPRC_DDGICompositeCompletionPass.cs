using XREngine.Rendering.GI.Contracts;
using XREngine.Rendering.GI.DDGI;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Publishes DDGI's GPU-use receipt only after the neutral compositor has drawn
/// the provider output.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_DDGICompositeCompletionPass : ViewportRenderCommand
{
    protected override bool ShouldExecuteThisFrame()
        => GlobalIlluminationCompositionState.TryGetComposited(ActivePipelineInstance, out _);

    protected override void Execute()
    {
        if (!GlobalIlluminationCompositionState.TryGetComposited(ActivePipelineInstance, out bool isDiagnostic))
            return;

        DDGIFrameContext context = DDGIFrameContext.Get(ActivePipelineInstance);
        if (!context.RecordCompositeUse())
            return;
        if (isDiagnostic)
            GlobalIlluminationDiagnosticPresentation.Mark(ActivePipelineInstance);
    }
}
