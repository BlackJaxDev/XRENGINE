namespace XREngine.Editor.Mcp;

/// <summary>
/// Identifies why a viewport sequence capture stopped accepting new frames.
/// </summary>
internal enum ViewportSequenceCaptureStopReason
{
    None,
    FrameCountReached,
    DurationElapsed,
    MaxFramesReached,
    UserCanceled,
    WindowClosing,
    BackpressureExceeded,
    PixelBudgetExceeded,
    RendererUnavailable,
    UnsupportedRenderer,
    CaptureError,
    FinalizationError,
}
