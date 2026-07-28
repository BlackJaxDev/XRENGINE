namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Durable accepted or rejected iteration entry.
/// </summary>
public sealed class SelfIterationAttemptRecord
{
    public int Iteration { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Fingerprint { get; init; } = string.Empty;
    public bool Accepted { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public SelfIterationAgentProposal Proposal { get; init; } = new();
    public SelfIterationAgentImplementation? Implementation { get; init; }
    public SelfIterationReloadResult? Reload { get; init; }
    public SelfIterationComparisonResult? Comparison { get; init; }
    public IReadOnlyList<string> ChangedPaths { get; init; } = [];
    public string EvidenceDirectory { get; init; } = string.Empty;
}
