namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>Capacity, use, fragmentation, and growth for one arena class.</summary>
public sealed record PhysicsChainBenchmarkArenaResourceMetrics
{
    public long CapacityBytes { get; init; }
    public long LiveBytes { get; init; }
    public long HighWaterBytes { get; init; }
    public double FragmentationPercent { get; init; }
    public long GrowthCount { get; init; }
}
