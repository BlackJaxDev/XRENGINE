namespace XREngine.Components;

/// <summary>
/// Explicit candidate-generation decision. Unsupported GPU ownership is a
/// visible capability failure and never authorizes a readback-backed CPU path.
/// </summary>
public readonly record struct PhysicsChainColliderBroadphaseDecision(
    PhysicsChainColliderBroadphaseOwner Owner,
    bool IsSupported,
    bool RequiresReadback);
