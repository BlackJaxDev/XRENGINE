namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    public enum OpenXrRuntimeState
    {
        DesktopOnly,
        XrInstanceReady,
        XrSystemReady,
        SessionCreated,
        SessionRunning,
        SessionStopping,
        SessionLost,
        RecreatePending,
        Unavailable,
    }
}
