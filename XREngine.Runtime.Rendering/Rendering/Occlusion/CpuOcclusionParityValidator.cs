namespace XREngine.Rendering.Occlusion;

/// <summary>
/// One command's membership in matched occlusion-disabled and occlusion-enabled
/// candidate sets, plus the view coverage that proved an enabled-set removal.
/// </summary>
public readonly record struct CpuOcclusionParitySample(
    OcclusionViewKey ViewKey,
    uint SourceCommandIndex,
    bool InDisabledCandidateSet,
    bool InEnabledCandidateSet,
    bool IsKnownVisibleSentinel,
    uint OcclusionProofCoverageMask);

/// <summary>Strict desktop/SPS acceptance result for an occlusion parity cohort.</summary>
public readonly record struct CpuOcclusionParityValidationResult(
    int SampleCount,
    int DisabledCandidateCount,
    int EnabledCandidateCount,
    int RemovedCandidateCount,
    int DesktopProvenCullCount,
    int SinglePassStereoProvenCullCount,
    int EnabledOutsideDisabledCount,
    int RemovedWithoutProofCount,
    int KnownVisibleSentinelCount,
    int RejectedSentinelCount,
    bool FinalImageParity)
{
    public bool IsValid
        => SampleCount > 0 &&
           FinalImageParity &&
           KnownVisibleSentinelCount > 0 &&
           DesktopProvenCullCount > 0 &&
           SinglePassStereoProvenCullCount > 0 &&
           EnabledOutsideDisabledCount == 0 &&
           RemovedWithoutProofCount == 0 &&
           RejectedSentinelCount == 0;
}

/// <summary>
/// Validates the set and proof invariants used by the Phase 5.2.4b desktop/SPS
/// occlusion-on versus occlusion-off acceptance run.
/// </summary>
public static class CpuOcclusionParityValidator
{
    public static CpuOcclusionParityValidationResult Validate(
        ReadOnlySpan<CpuOcclusionParitySample> samples,
        bool finalImageParity)
    {
        int disabledCandidateCount = 0;
        int enabledCandidateCount = 0;
        int removedCandidateCount = 0;
        int desktopProvenCullCount = 0;
        int singlePassStereoProvenCullCount = 0;
        int enabledOutsideDisabledCount = 0;
        int removedWithoutProofCount = 0;
        int knownVisibleSentinelCount = 0;
        int rejectedSentinelCount = 0;

        foreach (ref readonly CpuOcclusionParitySample sample in samples)
        {
            if (sample.InDisabledCandidateSet)
                disabledCandidateCount++;
            if (sample.InEnabledCandidateSet)
                enabledCandidateCount++;
            if (sample.InEnabledCandidateSet && !sample.InDisabledCandidateSet)
                enabledOutsideDisabledCount++;

            if (sample.IsKnownVisibleSentinel)
            {
                knownVisibleSentinelCount++;
                if (!sample.InDisabledCandidateSet || !sample.InEnabledCandidateSet)
                    rejectedSentinelCount++;
            }

            if (!sample.InDisabledCandidateSet || sample.InEnabledCandidateSet)
                continue;

            removedCandidateCount++;
            uint requiredCoverage = sample.ViewKey.RequiredCoverageMask;
            bool hasFullProof = requiredCoverage != 0u &&
                (sample.OcclusionProofCoverageMask & requiredCoverage) == requiredCoverage;

            if (IsDesktopScope(sample.ViewKey.Scope))
            {
                if (hasFullProof)
                    desktopProvenCullCount++;
                else
                    removedWithoutProofCount++;
                continue;
            }

            if (IsSinglePassStereoScope(sample.ViewKey.Scope) && HasMultipleBits(requiredCoverage))
            {
                if (hasFullProof)
                    singlePassStereoProvenCullCount++;
                else
                    removedWithoutProofCount++;
                continue;
            }

            removedWithoutProofCount++;
        }

        return new CpuOcclusionParityValidationResult(
            samples.Length,
            disabledCandidateCount,
            enabledCandidateCount,
            removedCandidateCount,
            desktopProvenCullCount,
            singlePassStereoProvenCullCount,
            enabledOutsideDisabledCount,
            removedWithoutProofCount,
            knownVisibleSentinelCount,
            rejectedSentinelCount,
            finalImageParity);
    }

    private static bool IsDesktopScope(EOcclusionViewScope scope)
        => scope is EOcclusionViewScope.MonoDesktop
            or EOcclusionViewScope.EditorDesktopWhileVr
            or EOcclusionViewScope.MirrorOnly;

    private static bool IsSinglePassStereoScope(EOcclusionViewScope scope)
        => scope is EOcclusionViewScope.VrSinglePassStereo
            or EOcclusionViewScope.VrFoveatedView;

    private static bool HasMultipleBits(uint mask)
        => mask != 0u && (mask & (mask - 1u)) != 0u;
}
