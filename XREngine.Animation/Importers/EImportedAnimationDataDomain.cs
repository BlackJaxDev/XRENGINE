namespace XREngine.Animation.Importers;

/// <summary>
/// Behaviorally distinct data domains carried by a Unity AnimationClip.
/// </summary>
public enum EImportedAnimationDataDomain
{
    GenericTransform,
    GenericProperty,
    HumanoidMuscle,
    HumanoidBody,
    HumanoidIK,
    RootMotionSettings,
    ClipMetadata,
    AnimationEvent,
    ObjectReference,
    SourceEncoding,
}
