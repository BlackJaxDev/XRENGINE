namespace XREngine.RenderBench;

/// <summary>Fail-closed summary of the evidence needed to promote a component optimization.</summary>
public sealed record RenderBenchPromotionReport(
    string Status,
    IReadOnlyList<string> MissingOrFailedEvidence,
    double ComponentSavingsMilliseconds,
    double? DemonstratedFullFrameSavingsMilliseconds)
{
    /// <summary>Assess supplied evidence; paths represent validated reports, not inferred passes.</summary>
    public static RenderBenchPromotionReport Evaluate(
        RenderBenchPromotionEvidence? evidence,
        double componentSavingsMilliseconds,
        double? demonstratedFullFrameSavingsMilliseconds,
        IReadOnlyDictionary<string, bool> validatedLaneComparisons)
    {
        List<string> issues = [];
        if (evidence is null)
        {
            issues.Add("Promotion evidence is missing.");
            return new("Unresolved", issues, componentSavingsMilliseconds, demonstratedFullFrameSavingsMilliseconds);
        }
        if (!evidence.CorrectnessPassed) issues.Add("Component correctness failed or is missing.");
        if (!evidence.OperationCountsPassed) issues.Add("Operation-count gate failed or is missing.");
        if (!evidence.AllocationsPassed) issues.Add("Allocation gate failed or is missing.");
        if (!evidence.StabilityPassed) issues.Add("Stability gate failed or is missing.");
        if (!evidence.ValidationPassed) issues.Add("Validation cohort failed or is missing.");
        if (!evidence.SynchronizationValidationPassed) issues.Add("Synchronization-validation cohort failed or is missing.");
        if (evidence.Intrusive) issues.Add("Intrusive captures cannot establish clean promotion evidence.");
        if (evidence.SyntheticProxy) issues.Add("Synthetic proxy evidence cannot establish a whole-frame gain.");
        RequireLane("subsystem", evidence.SubsystemReportPath);
        RequireLane("presentationless", evidence.PresentationlessReportPath);
        RequireLane("desktop_wsi", evidence.DesktopReportPath);
        if (evidence.AffectsXr) RequireLane("open_xr", evidence.OpenXrReportPath);
        if (string.IsNullOrWhiteSpace(evidence.FullFrameSavingsEvidencePath) ||
            !demonstratedFullFrameSavingsMilliseconds.HasValue)
            issues.Add("Measured full-frame savings are missing.");
        if (!double.IsFinite(componentSavingsMilliseconds) || componentSavingsMilliseconds <= 0)
            issues.Add("No finite positive component saving was demonstrated.");
        if (!demonstratedFullFrameSavingsMilliseconds.HasValue ||
            !double.IsFinite(demonstratedFullFrameSavingsMilliseconds.Value) ||
            demonstratedFullFrameSavingsMilliseconds.Value <= 0)
            issues.Add("No finite positive full-frame saving was demonstrated.");
        if (!string.IsNullOrWhiteSpace(evidence.RejectionReason)) issues.Add(evidence.RejectionReason);
        return new(issues.Count == 0 ? "Eligible" : "Unresolved", issues,
            componentSavingsMilliseconds, demonstratedFullFrameSavingsMilliseconds);

        void RequireLane(string lane, string? reportPath)
        {
            if (string.IsNullOrWhiteSpace(reportPath) ||
                !validatedLaneComparisons.TryGetValue(lane, out bool passed) || !passed)
                issues.Add($"Validated {lane} comparison is missing or failed.");
        }
    }
}
