namespace XREngine.Rendering;

/// <summary>
/// Capture-visible portions of the direct-FBO command chain.
/// </summary>
public enum ERenderCapturePass
{
    PreRender,
    Background,
    OpaqueDeferred,
    OpaqueForward,
    Masked,
    Transparent,
    OnTop,
    ComputeLighting,
    DebugOverlays,
    PostRender,
    ScreenSpaceUi,
}
