namespace XREngine.Rendering;

/// <summary>
/// Identifies the intended consumer of an offscreen scene render.
/// </summary>
public enum ERenderCaptureKind
{
    None,
    SceneCapture,
    LightProbe,
    ReflectionProbe,
    GiProbe,
    ThumbnailOrUiPreview,
    DiagnosticFbo,
}
