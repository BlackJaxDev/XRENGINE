namespace XREngine.Editor.Benchmarks.PhysicsChain;

public sealed record PhysicsChainBenchmarkGpuStageMetrics
{
    public double ActiveListMilliseconds { get; init; }
    public double SimulationMilliseconds { get; init; }
    public double CollisionMilliseconds { get; init; }
    public double PaletteMilliseconds { get; init; }
    public double BoundsMilliseconds { get; init; }
    public double SkinningMilliseconds { get; init; }
    public double CullingMilliseconds { get; init; }
    public double DrawMilliseconds { get; init; }
    public long DispatchCount { get; init; }
    public long WorkgroupCount { get; init; }
    public long ActiveLaneCount { get; init; }
    public long BarrierCount { get; init; }
    public long UploadBytes { get; init; }
    public long CopyBytes { get; init; }
    public long ReadbackBytes { get; init; }
    public long FenceWaitCount { get; init; }
}
