namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Acceptance policy layered on the per-run timing configuration.
/// </summary>
public sealed record PhysicsChainBenchmarkRunPolicy
{
    public int MatchedRunCount { get; init; } = 3;
    public bool RequireReleaseBuild { get; init; } = true;
    public bool RequireProductionDiagnostics { get; init; } = true;

    public void Validate()
        => ArgumentOutOfRangeException.ThrowIfLessThan(MatchedRunCount, 3);
}
