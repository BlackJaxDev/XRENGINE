namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>Hardware-counter evidence captured by ETW or an equivalent profiler.</summary>
public sealed record PhysicsChainBenchmarkCpuHardwareMetrics
{
    public static PhysicsChainBenchmarkCpuHardwareMetrics Unavailable { get; } = new();

    public PhysicsChainBenchmarkMetricAvailability Availability { get; init; }
    public string? UnavailableReason { get; init; }
    public long CacheMisses { get; init; }
    public long BranchInstructions { get; init; }
    public long BranchMisses { get; init; }
    public long ContextSwitches { get; init; }
    public long ThreadMigrations { get; init; }
    public double MemoryBandwidthBytesPerSecond { get; init; }
    public int EffectiveSimdWidthBits { get; init; }
    public string? TracePath { get; init; }
}
