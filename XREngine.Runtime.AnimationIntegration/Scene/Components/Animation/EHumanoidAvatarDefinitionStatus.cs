namespace XREngine.Components.Animation;

/// <summary>
/// Validation state of the component-owned humanoid avatar definition.
/// </summary>
public enum EHumanoidAvatarDefinitionStatus
{
    Uninitialized,
    NeedsReview,
    Valid,
    Invalid,
    SkeletonMismatch,
    SourceMismatch,
}
