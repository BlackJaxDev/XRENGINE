namespace XREngine.Rendering.Occlusion;

/// <summary>Allocation-free result of evaluating one offline calibration bucket.</summary>
public readonly record struct GpuHiZSelectorDecision(
    EGpuHiZCandidateMode Candidate,
    EGpuHiZSelectorDecisionReason Reason,
    GpuHiZSelectorProfile? Profile)
{
    public bool IsSelected => Reason == EGpuHiZSelectorDecisionReason.Selected;
}
