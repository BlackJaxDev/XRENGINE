using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

public abstract partial class RenderPipeline
{
    /// <summary>
    /// Appends the pipeline-agnostic <see cref="VPRC_PrepareUiBackdropBlur"/> command immediately
    /// before <see cref="VPRC_RenderScreenSpaceUI"/> so any pipeline that renders screen-space UI
    /// gains on-demand frosted-glass backdrop blur with zero idle GPU cost.
    /// </summary>
    protected static VPRC_RenderScreenSpaceUI AppendScreenSpaceUiWithBackdropBlur(
        ViewportRenderCommandContainer commands,
        string[]? sourceTextureNames = null,
        string? screenSpaceUiAnchorId = null)
    {
        VPRC_PrepareUiBackdropBlur backdropBlur = commands.Add<VPRC_PrepareUiBackdropBlur>();
        backdropBlur.SourceTextureNames = sourceTextureNames;

        VPRC_RenderScreenSpaceUI screenSpaceUi = commands.Add<VPRC_RenderScreenSpaceUI>();
        if (!string.IsNullOrEmpty(screenSpaceUiAnchorId))
            screenSpaceUi.WithAnchor(screenSpaceUiAnchorId);

        return screenSpaceUi;
    }
}
