using System;

namespace XREngine.Rendering.Occlusion;

/// <summary>
/// One completed, parity-proven matched capture. Both costs must come from the
/// same timestamp scope; CPU command-recording times are deliberately invalid.
/// </summary>
public readonly record struct GpuHiZMatchedCrossoverSample(
    GpuHiZCalibrationBucket Bucket,
    EGpuHiZCandidateMode Candidate,
    string ParityProofSource,
    string MatchedCohortFingerprint,
    string TimestampScope,
    uint CompletedMatchedFrames,
    double DisabledGpuNanoseconds,
    double CandidateGpuNanoseconds)
{
    public bool IsValid =>
        Bucket.IsValid &&
        Candidate is EGpuHiZCandidateMode.Full or EGpuHiZCandidateMode.Coarse &&
        !string.IsNullOrWhiteSpace(ParityProofSource) &&
        !string.IsNullOrWhiteSpace(MatchedCohortFingerprint) &&
        !string.IsNullOrWhiteSpace(TimestampScope) &&
        CompletedMatchedFrames > 0u &&
        double.IsFinite(DisabledGpuNanoseconds) && DisabledGpuNanoseconds > 0.0 &&
        double.IsFinite(CandidateGpuNanoseconds) && CandidateGpuNanoseconds > 0.0;
}
