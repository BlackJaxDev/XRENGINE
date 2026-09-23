namespace XREngine.Rendering.Vulkan;

/// <summary>
/// On-demand telemetry for the immutable authoring-lease to frame-plan input
/// copy. Timings cover only the six retained input columns, after fixed-slot
/// capacity has been validated.
/// </summary>
public readonly record struct VulkanAdvancedVisibilityInputCopyDiagnosticsSnapshot(
    long CopyCount,
    long CopyBytes,
    double CopyMilliseconds,
    double MaximumCopyMilliseconds,
    long RejectedCopyCount);
