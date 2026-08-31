namespace XREngine.RenderBench;

/// <summary>Quantitative cold-path verdict for one aligned E/V/O/K visibility frame.</summary>
public sealed record RenderBenchVisibilityFrameVerdict
{
    public int Step { get; init; }
    public int EligibilityCount { get; init; }
    public int VisibleCount { get; init; }
    public int OccludedCount { get; init; }
    public int KeptCount { get; init; }
    public int RenderedCount { get; init; }
    public int EarlyCount { get; init; }
    public int LateCount { get; init; }
    public int FalseOcclusionCount { get; init; }
    public int MissingVisibleCount { get; init; }
    public int ConservativeOverdrawCount { get; init; }
    public int DemonstratedCullCount { get; init; }
    public int HeavyCandidateCullCount { get; init; }
    public bool HeavyCandidateStreamCoverageComplete { get; init; }
    public bool TwoPassExecuted { get; init; }
    public bool TemporalInvalidated { get; init; }
    public bool ConservativeEarlyCoverageProven { get; init; }
    public bool ReceiptProvenanceAvailable { get; init; }
    public bool Passed { get; init; }
    public string[] Failures { get; init; } = [];
}
