namespace XREngine;

public readonly record struct ViewBatchSplitPolicy(
    uint MinimumCandidateSamples,
    double GeometryWorkCost,
    double AddedSubmissionCost,
    double SplitEnterRatio,
    double SplitExitRatio)
{
    public static ViewBatchSplitPolicy Default
        => new(64u, 1.0, 250.0, 1.15, 0.85);

    public ViewBatchSplitPolicy Validate()
    {
        if (!double.IsFinite(GeometryWorkCost) || GeometryWorkCost < 0.0)
            throw new ArgumentOutOfRangeException(nameof(GeometryWorkCost));
        if (!double.IsFinite(AddedSubmissionCost) || AddedSubmissionCost < 0.0)
            throw new ArgumentOutOfRangeException(nameof(AddedSubmissionCost));
        if (!double.IsFinite(SplitEnterRatio) || SplitEnterRatio <= 1.0)
            throw new ArgumentOutOfRangeException(nameof(SplitEnterRatio));
        if (!double.IsFinite(SplitExitRatio) || SplitExitRatio < 0.0 || SplitExitRatio >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(SplitExitRatio));
        return this;
    }
}
