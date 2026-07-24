namespace XREngine.Components;

public enum PhysicsChainReadbackStatus : byte
{
    Pending,
    InFlight,
    Available,
    Cancelled,
    Expired,
    DiscardedStale,
    Failed,
}
