namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stable, allocation-free reason codes produced by desktop frame-loop policies.
/// </summary>
internal enum EVulkanDesktopPolicyReason
{
    Ready,
    Reentrant,
    ZeroSurface,
    ResizePending,
    ResourceMismatch,
    InteractiveSlotBusy,
    SurfaceUnavailable,
    AcquireSuccess,
    AcquireSuboptimal,
    AcquireNotReady,
    AcquireTimeout,
    AcquireOutOfDate,
    AcquireSurfaceLost,
    AcquireDeviceLost,
    AcquireUnexpected,
    PresentSuccess,
    PresentSuboptimal,
    PresentOutOfDate,
    PresentSurfaceLost,
    PresentDeviceLost,
    PresentUnexpected,
    ImagePreparationFailed,
    RecordingFailed,
    SubmissionFailed,
    PostSubmitAuxiliaryFailed,
    PostPresentAuxiliaryFailed,
}
