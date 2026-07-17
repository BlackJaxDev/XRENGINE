namespace XREngine.Rendering.Occlusion;

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
