namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stable terminal classification for desktop frame outcomes. This is kept
/// separate from <see cref="EDesktopFrameReason"/>, which identifies the exact
/// orchestration branch that ended the attempt.
/// </summary>
internal enum EVulkanDesktopFrameFailureKind : byte
{
    None,
    NoImageAvailable,
    OutOfDate,
    SurfaceLost,
    DeviceLost,
    HostOutOfMemory,
    DeviceOutOfMemory,
    CallerCanceled,
    AdmissionDeferred,
    ReadinessFailed,
    RecordingFailed,
    SubmissionFailed,
    PresentationFailed,
    Unexpected,
}
