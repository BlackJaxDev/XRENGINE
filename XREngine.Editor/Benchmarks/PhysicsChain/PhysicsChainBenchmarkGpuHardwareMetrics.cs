namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>GPU profiler evidence for retained physics-chain kernel buckets.</summary>
public sealed record PhysicsChainBenchmarkGpuHardwareMetrics
{
    public static PhysicsChainBenchmarkGpuHardwareMetrics Unavailable { get; } = new();

    public PhysicsChainBenchmarkMetricAvailability Availability { get; init; }
    public string? UnavailableReason { get; init; }
    public double OccupancyPercent { get; init; }
    public int RegistersPerThread { get; init; }
    public long RegisterSpillBytes { get; init; }
    public long SharedMemoryBytesPerWorkgroup { get; init; }
    public double MemoryBandwidthBytesPerSecond { get; init; }
    public long BufferBarrierCount { get; init; }
    public long ImageBarrierCount { get; init; }
    public long IndirectArgumentBarrierCount { get; init; }
    public long IndirectCommandCount { get; init; }
    public string? TracePath { get; init; }
}
