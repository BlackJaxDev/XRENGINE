namespace XREngine;

public enum EHiZOcclusionDisposition : byte
{
    Disabled,
    BypassConservatively,
    SampleOwnHistory,
    SampleValidatedOuterEyeHistory,
    BuildCurrentFrameOuterEye,
}
