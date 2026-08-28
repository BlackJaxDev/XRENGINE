namespace XREngine.Rendering.Vulkan;

/// <summary>Monotonic resident-template table counters captured for telemetry.</summary>
internal readonly record struct VulkanResidentDrawTemplateTelemetrySnapshot(
    ulong Hits,
    ulong Misses,
    ulong Creates,
    ulong Replacements,
    ulong Evictions,
    ulong FullStructuralComparisons,
    ulong DependencyRejects,
    ulong CapacityFailures,
    int ResidentTemplateCount,
    ulong ExactDependencyInvalidations,
    ulong BroadFallbackInvalidations,
    ulong BroadFallbackEntries);
