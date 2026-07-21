namespace XREngine;

[Flags]
public enum EHiZHistoryInvalidation : ushort
{
    None = 0,
    CameraCut = 1 << 0,
    TrackingJump = 1 << 1,
    ProjectionDiscontinuity = 1 << 2,
    ResourceGenerationChanged = 1 << 3,
    UnsafeSceneRevision = 1 << 4,
    PeriodicValidation = 1 << 5,
    MissingHistory = 1 << 6,
    InsetRelationshipUnproven = 1 << 7,
}
