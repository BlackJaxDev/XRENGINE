namespace XREngine.Rendering;

/// <summary>
/// Non-blocking authority for overwriting backend-owned GPU buffer contents.
/// </summary>
public enum EGpuBufferContentReuseStatus
{
    Ready,
    AwaitingSubmission,
    PendingCompletion,
    Superseded,
    DeviceLost,
    Unsupported,
}