using XREngine.Data.Rendering;

namespace XREngine.Components.Lights;

/// <summary>Captures the canonical Advanced RG32_UINT surface identity without color shading or post processing.</summary>
public sealed class VisibilityCaptureComponent : AdvancedOffscreenTextureCaptureComponent
{
    protected override RenderPipelineOffscreenIntent OffscreenIntent
        => RenderPipelineOffscreenIntent.Visibility();

    protected override EFrameOutputKind CaptureOutputKind => EFrameOutputKind.SceneCapture;
}
