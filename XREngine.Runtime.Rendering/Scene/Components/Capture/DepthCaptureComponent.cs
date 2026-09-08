using XREngine.Data.Rendering;

namespace XREngine.Components.Lights;

/// <summary>
/// Captures canonical Advanced depth into a completion-gated D32F/S8 texture.
/// Only depth is exported; stencil contents are not part of this product.
/// </summary>
public sealed class DepthCaptureComponent : AdvancedOffscreenTextureCaptureComponent
{
    protected override RenderPipelineOffscreenIntent OffscreenIntent
        => RenderPipelineOffscreenIntent.Depth();

    protected override EFrameOutputKind CaptureOutputKind => EFrameOutputKind.SceneCapture;
}
