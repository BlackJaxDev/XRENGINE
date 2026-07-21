namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>Capacity and lifetime diagnostics sampled at the run high-water mark.</summary>
public sealed record PhysicsChainBenchmarkArenaMetrics
{
    public int InstanceCapacity { get; init; }
    public int InstanceLiveCount { get; init; }
    public int ParticleCapacity { get; init; }
    public int ParticleLiveCount { get; init; }
    public int TemplateCapacity { get; init; }
    public int TemplateLiveCount { get; init; }
    public int ColliderCapacity { get; init; }
    public int ColliderLiveCount { get; init; }
    public int OutputCapacity { get; init; }
    public int OutputLiveCount { get; init; }
    public double FragmentationPercent { get; init; }
    public long GrowthCount { get; init; }
    public long CapacityBytes { get; init; }
    public long LiveBytes { get; init; }
    public long HighWaterBytes { get; init; }
    public PhysicsChainBenchmarkMetricAvailability ResourceBreakdownAvailability { get; init; }
    public PhysicsChainBenchmarkArenaResourceMetrics Static { get; init; } = new();
    public PhysicsChainBenchmarkArenaResourceMetrics State { get; init; } = new();
    public PhysicsChainBenchmarkArenaResourceMetrics Collider { get; init; } = new();
    public PhysicsChainBenchmarkArenaResourceMetrics Palette { get; init; } = new();
    public PhysicsChainBenchmarkArenaResourceMetrics Bounds { get; init; } = new();
    public PhysicsChainBenchmarkArenaResourceMetrics Readback { get; init; } = new();

}
