namespace XREngine.Editor.Benchmarks.PhysicsChain;

public sealed record PhysicsChainBenchmarkPopulationMetrics
{
    public int ActiveChains { get; init; }
    public int RateLimitedChains { get; init; }
    public int SleepingChains { get; init; }
    public int CulledChains { get; init; }
    public int WokenChains { get; init; }
    public int ActiveParticles { get; init; }
    public long PaletteDispatches { get; init; }
    public long SkinningDispatches { get; init; }
    public long RendererSubmissions { get; init; }
    public long DrawCount { get; init; }
    public long TriangleCount { get; init; }
}
