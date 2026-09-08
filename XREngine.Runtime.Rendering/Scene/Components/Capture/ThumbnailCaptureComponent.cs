using XREngine.Data.Rendering;

namespace XREngine.Components.Lights;

/// <summary>
/// Owns a standalone Advanced thumbnail texture with no temporal, bloom, or
/// main-view post-processing dependency.
/// </summary>
public sealed class ThumbnailCaptureComponent : AdvancedOffscreenTextureCaptureComponent
{
    protected override RenderPipelineOffscreenIntent OffscreenIntent
        => RenderPipelineOffscreenIntent.Thumbnail();

    protected override EFrameOutputKind CaptureOutputKind => EFrameOutputKind.Thumbnail;
}
