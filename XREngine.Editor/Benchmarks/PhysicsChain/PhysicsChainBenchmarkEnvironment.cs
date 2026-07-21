namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Exact named-hardware and presentation context stored with every result.
/// Fields not discoverable portably are required launcher inputs.
/// </summary>
public sealed record PhysicsChainBenchmarkEnvironment
{
    public required string CpuModel { get; init; }
    public required int PhysicalCoreCount { get; init; }
    public required int LogicalCoreCount { get; init; }
    public required string CoreTopology { get; init; }
    public required long MemoryBytes { get; init; }
    public required string MemoryConfiguration { get; init; }
    public required string GpuModel { get; init; }
    public required string GpuDriver { get; init; }
    public required string WindowsVersion { get; init; }
    public required string PowerMode { get; init; }
    public required PhysicsChainBenchmarkRenderBackend RenderBackend { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int RefreshRateHz { get; init; }
    public required bool VSyncEnabled { get; init; }
    public required string BuildConfiguration { get; init; }
    public required string Commit { get; init; }
}
