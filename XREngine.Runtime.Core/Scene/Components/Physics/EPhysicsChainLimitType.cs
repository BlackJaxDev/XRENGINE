namespace XREngine.Components;

/// <summary>
/// Selects how far a chain segment can turn from its rest direction.
/// </summary>
public enum EPhysicsChainLimitType
{
    /// <summary>The segment has no angle limit.</summary>
    None,
    /// <summary>The segment stays inside a cone around the limit axis.</summary>
    Angle,
    /// <summary>The segment stays on a plane, inside an arc around the limit axis.</summary>
    Hinge,
    /// <summary>The segment stays inside separate pitch and yaw ranges around the limit axis.</summary>
    Polar,
}
