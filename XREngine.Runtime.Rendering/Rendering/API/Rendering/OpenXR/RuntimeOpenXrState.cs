namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Lifecycle state exposed to runtime and editor consumers.</summary>
public enum RuntimeOpenXrState
{
    DesktopOnly = 0,
    XrInstanceReady = 1,
    XrSystemReady = 2,
    SessionCreated = 3,
    SessionRunning = 4,
    SessionStopping = 5,
    SessionLost = 6,
    RecreatePending = 7,
    Unavailable = 8,
}
