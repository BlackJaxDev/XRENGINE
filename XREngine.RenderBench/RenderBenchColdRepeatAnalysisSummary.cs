namespace XREngine.RenderBench;

/// <summary>Cold-repeat determinism evidence across complete scenario lanes.</summary>
public sealed record RenderBenchColdRepeatAnalysisSummary
{
    /// <summary>False when the matrix contains only non-image buffer probes.</summary>
    public bool Applicable { get; init; }
    /// <summary>"passed", "failed", or "not-applicable".</summary>
    public string Status { get; init; } = "not-applicable";
    public int ComparedLaneCount { get; init; }
    public int ComparedFrameCount { get; init; }
    public int MismatchedFrameCount { get; init; }
    public bool IdentityMatched { get; init; }
    public bool Passed { get; init; }
}
