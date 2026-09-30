namespace XREngine;

/// <summary>
/// Explains whether a view's previous transform belongs to a compatible preceding output.
/// </summary>
public enum ERenderFrameViewHistoryStatus
{
    Unavailable = 0,
    Valid,
    FirstObservation,
    FrameGap,
    CameraChanged,
    CameraCut,
    OutputChanged,
    TrackingInvalid,
}
