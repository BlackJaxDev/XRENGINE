namespace XREngine.RenderBench;

/// <summary>Intrusive Vulkan performance-query replay evidence from an immutable control fixture.</summary>
public sealed record RenderBenchPerformanceCounterSnapshot(
    string Status,
    string? UnsupportedReason,
    uint QueueFamilyIndex,
    uint PassCount,
    string Fixture,
    string Extension,
    IReadOnlyList<RenderBenchPerformanceCounterMetadata> AvailableCounters,
    IReadOnlyList<RenderBenchPerformanceCounterValue> Counters);
