namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    public enum OpenXrRuntimeLossReason
    {
        None,
        SessionExiting,
        SessionLossPending,
        SessionLostError,
        InstanceLostError,
        RuntimeUnavailable,
        ShutdownRequested
    }
}
