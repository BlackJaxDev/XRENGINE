using XREngine.Data.Rendering;

namespace XREngine.Components.Lights;

/// <summary>
/// Owns a portal's isolated Advanced HDR capture. The owner never reuses the
/// source viewport or attachment, preventing portal feedback into its source view.
/// </summary>
public sealed class PortalCaptureComponent : AdvancedOffscreenTextureCaptureComponent
{
    protected override RenderPipelineOffscreenIntent OffscreenIntent
        => new(ERenderPipelineOffscreenViewIntent.Portal,
            ERenderPipelineOffscreenOutput.HdrColor,
            EnableLateTransparency: true);

    protected override EFrameOutputKind CaptureOutputKind => EFrameOutputKind.SceneCapture;
}
