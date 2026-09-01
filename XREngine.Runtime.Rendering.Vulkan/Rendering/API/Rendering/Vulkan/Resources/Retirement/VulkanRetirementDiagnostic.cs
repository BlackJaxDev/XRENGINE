namespace XREngine.Rendering.Vulkan;

/// <summary>On-demand retirement and current desktop presentation proof diagnostics.</summary>
public sealed record VulkanRetirementDiagnostic(
    long FrameSerial,
    double DrainElapsedMilliseconds,
    long DrainDurationSampleCount,
    long DrainDurationOverflowCount,
    double MaximumPublishedDrainDurationMilliseconds,
    double DrainDurationP50Milliseconds,
    double DrainDurationP95Milliseconds,
    double DrainDurationP99Milliseconds,
    VulkanRetirementClassDiagnostic[] Classes,
    int QuarantinedFailures,
    bool PresentationMaintenanceEnabled,
    ulong DesktopGeneration,
    long CurrentGenerationPresentsSubmitted,
    long CurrentGenerationPresentsCompleted,
    long CurrentGenerationCapacityDeferrals,
    bool HasUnprovenLegacyPresent,
    long DeviceWaitIdleCalls);
