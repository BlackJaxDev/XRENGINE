namespace XREngine.Components.Animation;

/// <summary>
/// Selects who consumes the projected root pose produced by a humanoid animation clip.
/// </summary>
public enum EHumanoidRootMotionApplicationMode
{
    /// <summary>
    /// Keep projected pose and delta data available without moving a transform or publishing an event.
    /// </summary>
    ExtractOnly,

    /// <summary>
    /// Apply the projected pose to <see cref="AnimationClipComponent.RootMotionTarget"/>
    /// relative to the target pose captured at the start of the playback epoch.
    /// </summary>
    ApplyToExplicitTarget,

    /// <summary>
    /// Publish each evaluated pose through <see cref="AnimationClipComponent.RootMotionEvaluated"/>
    /// and leave scene placement to the subscriber.
    /// </summary>
    ExternalConsumer,
}
