namespace XREngine.RenderBench;

/// <summary>Explicit promotion evidence; absent lanes must never imply a pass.</summary>
public sealed record RenderBenchPromotionEvidence(
    bool CorrectnessPassed,
    bool OperationCountsPassed,
    bool AllocationsPassed,
    bool StabilityPassed,
    bool ValidationPassed,
    bool SynchronizationValidationPassed,
    string? SubsystemReportPath,
    string? PresentationlessReportPath,
    string? DesktopReportPath,
    string? OpenXrReportPath,
    bool AffectsXr,
    string? FullFrameSavingsEvidencePath,
    string? RejectionReason)
{
    /// <summary>Intrusive observer results cannot establish a clean whole-frame gain.</summary>
    public bool Intrusive { get; init; }
    /// <summary>Synthetic fixture evidence is local diagnostic evidence, not a full-frame result.</summary>
    public bool SyntheticProxy { get; init; }
}
