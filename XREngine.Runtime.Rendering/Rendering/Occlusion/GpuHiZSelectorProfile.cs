using System;

namespace XREngine.Rendering.Occlusion;

/// <summary>Immutable evidence summary for one calibrated Hi-Z candidate.</summary>
public sealed record GpuHiZSelectorProfile(
    EGpuHiZCandidateMode Candidate,
    string ParityProofSource,
    string MatchedCohortFingerprint,
    string TimestampScope,
    uint CompletedMatchedFrames,
    uint PairedWinSamples,
    double MedianDisabledGpuNanoseconds,
    double MedianCandidateGpuNanoseconds,
    double WorstPairedSavingsNanoseconds)
{
    public bool Meets(in GpuHiZCrossoverRequirements requirements)
    {
        requirements.Validate();
        if (Candidate is not (EGpuHiZCandidateMode.Full or EGpuHiZCandidateMode.Coarse) ||
            string.IsNullOrWhiteSpace(ParityProofSource) ||
            string.IsNullOrWhiteSpace(MatchedCohortFingerprint) ||
            string.IsNullOrWhiteSpace(TimestampScope) ||
            CompletedMatchedFrames < requirements.MinimumCompletedMatchedFrames ||
            PairedWinSamples < requirements.MinimumPairedWinSamples ||
            !double.IsFinite(MedianDisabledGpuNanoseconds) || MedianDisabledGpuNanoseconds <= 0.0 ||
            !double.IsFinite(MedianCandidateGpuNanoseconds) || MedianCandidateGpuNanoseconds <= 0.0 ||
            !double.IsFinite(WorstPairedSavingsNanoseconds))
            return false;

        double medianSavings = MedianDisabledGpuNanoseconds - MedianCandidateGpuNanoseconds;
        double relativeSavings = medianSavings / MedianDisabledGpuNanoseconds;
        return medianSavings >= requirements.MinimumAbsoluteSavingsNanoseconds &&
               relativeSavings >= requirements.MinimumRelativeSavings &&
               WorstPairedSavingsNanoseconds >= 0.0;
    }
}
