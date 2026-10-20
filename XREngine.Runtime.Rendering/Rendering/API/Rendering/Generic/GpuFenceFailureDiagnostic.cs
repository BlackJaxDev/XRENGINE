namespace XREngine.Rendering;

/// <summary>
/// Value-only evidence for the first failure of one fence rental.
/// NativeResult is valid only when NativeResultValid is true. It then contains
/// the Vulkan result from the failing queue submit or timeline query.
/// </summary>
public readonly record struct GpuFenceFailureDiagnostic(
    long RentalId,
    ulong AuthoredFrame,
    int AuthoredPass,
    ulong FailureFrame,
    EGpuFenceFailureSite Site,
    EGpuFenceSubmissionStatus StatusBeforeFailure,
    ulong CommandBuffer,
    EGpuFenceNativeSubmission NativeSubmission,
    int NativeResult,
    bool NativeResultValid,
    ulong TimelineSemaphore,
    ulong TimelineValue);
