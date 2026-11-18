namespace XREngine.Rendering.Vulkan;

/// <summary>On-demand retirement and current desktop presentation proof diagnostics.</summary>
/// <param name="SupersededResourceGenerationsDrained">Retired resource generations that the descriptor cache owner drain took from its queue, since launch.</param>
/// <param name="SupersededDescriptorOwnersReleased">Descriptor cache owners that the drain released, since launch. When resource generations change and this value stays flat, retired images stay pinned by descriptor sets.</param>
/// <param name="SupersededDescriptorOwnerDrainDeferrals">OpenXR boundaries that left the drain for later because a desktop frame was recording, since launch.</param>
/// <param name="PendingSupersededResourceGenerations">Retired resource generations that wait for the next drain.</param>
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
    long DeviceWaitIdleCalls,
    int LiveResourceCount,
    int TrackedDescriptorSetCount,
    int PendingRetirementCount,
    long OldestPendingRetirementAgeMilliseconds,
    long SupersededResourceGenerationsDrained,
    long SupersededDescriptorOwnersReleased,
    long SupersededDescriptorOwnerDrainDeferrals,
    int PendingSupersededResourceGenerations);
