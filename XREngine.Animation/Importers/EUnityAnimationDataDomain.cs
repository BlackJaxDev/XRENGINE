namespace XREngine.Animation.Importers;

/// <summary>
/// Behaviorally distinct data domains carried by a Unity AnimationClip.
/// </summary>
public enum EUnityAnimationDataDomain
{
    GenericTransform,
    GenericProperty,
    HumanoidMuscle,
    HumanoidBody,
    HumanoidIK,
    RootMotionSettings,
    AnimationEvent,
    ObjectReference,
    SourceEncoding,
}
