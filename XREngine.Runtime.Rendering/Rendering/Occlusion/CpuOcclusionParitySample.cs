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
