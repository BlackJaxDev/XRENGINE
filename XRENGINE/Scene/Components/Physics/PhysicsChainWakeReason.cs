namespace XREngine.Components;

/// <summary>
/// Observable reason that most recently reset a chain's automatic-sleep state.
/// </summary>
public enum PhysicsChainWakeReason
{
    None,
    ExplicitRequest,
    ActivityExceeded,
    AccumulatedError,
    RootMovement,
    RootTeleport,
    RootAcceleration,
    RootConfigurationChanged,
    ExternalForceChanged,
    ForceOrEventInput,
    ColliderConfigurationChanged,
    ColliderMovement,
    ColliderShapeChanged,
    ColliderPoseChanged,
    AuthoredParameterChanged,
    SleepPolicyChanged,
    QualityPolicyChanged,
    RelevanceChanged,
    VisibilityOrUse,
}
