namespace XREngine;

/// <summary>
/// Frame-slot-owned visibility result. The masks intentionally preserve each
/// filtering stage so a pass cannot mistake eligibility for exact visibility.
/// </summary>
public readonly record struct FrameVisibilityCandidate(
    ulong StableInstanceId,
    ulong StableDrawId,
    ulong PassEligibilityMask,
    ulong FrustumVisibilityMask,
    ulong OcclusionVisibilityMask,
    ulong RenderBatchMembershipMask,
    uint FrameSlot,
    ulong Generation)
{
    public ulong ExactActiveViewMask
        => FrustumVisibilityMask & OcclusionVisibilityMask & RenderBatchMembershipMask;
}
