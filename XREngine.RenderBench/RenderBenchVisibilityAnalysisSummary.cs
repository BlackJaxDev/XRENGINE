namespace XREngine.RenderBench;

/// <summary>Aggregate, non-performance correctness evidence for an E/V/O/K cohort.</summary>
public sealed record RenderBenchVisibilityAnalysisSummary
{
    /// <summary>Depth convention used by this independent cohort.</summary>
    public string Depth { get; init; } = string.Empty;
    public string Workload { get; init; } = RenderBenchScenarioWorkloads.Default;
    /// <summary>True only when all three lanes passed and retained equal frame counts.</summary>
    public bool CohortComplete { get; init; }
    /// <summary>Number of aligned completed frames analyzed from the common prefix.</summary>
    public int FrameCount { get; init; }
    public int EligibilityCount { get; init; }
    public int VisibleCount { get; init; }
    public int OccludedCount { get; init; }
    public int KeptCount { get; init; }
    public int RenderedCount { get; init; }
    public int FalseOcclusionCount { get; init; }
    public int MissingVisibleCount { get; init; }
    public int ConservativeOverdrawCount { get; init; }
    public int DemonstratedCullCount { get; init; }
    public int HeavyCandidateCullCount { get; init; }
    public int TwoPassFrameCount { get; init; }
    public int LaterTwoPassFrameCount { get; init; }
    public bool DeterministicIdentityMatched { get; init; }
    public bool ReceiptProvenanceAvailable { get; init; }
    public bool Passed { get; init; }
    public RenderBenchVisibilityFrameVerdict[] Frames { get; init; } = [];
}
