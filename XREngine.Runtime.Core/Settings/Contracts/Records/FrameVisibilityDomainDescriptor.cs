namespace XREngine;

public readonly record struct FrameVisibilityDomainDescriptor(
    EFrameVisibilityDomain Domain,
    ulong StableDomainKey,
    ulong SceneRevision,
    uint FrameSlot,
    ulong Generation,
    int CandidateCapacity,
    EFrameVisibilityOverflowPolicy OverflowPolicy)
{
    public bool SharesMainCameraTraversal
        => Domain == EFrameVisibilityDomain.MainCamera;
}
