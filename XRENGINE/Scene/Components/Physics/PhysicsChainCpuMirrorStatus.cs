namespace XREngine.Components;

/// <summary>
/// Freshness state of an optional CPU compatibility mirror. GPU rendering does
/// not depend on this state.
/// </summary>
public enum PhysicsChainCpuMirrorStatus : byte
{
    Disabled,
    Requested,
    InFlight,
    Available,
    Stale,
    Unavailable,
}
