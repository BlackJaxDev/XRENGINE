namespace XREngine.Components;

/// <summary>
/// Cumulative request-lifecycle counters for one physics-chain world.
/// Each value counts requests, not selected elements or transferred bytes.
/// Requested counts every submission attempt; coalesced and rejected are
/// subsets. The remaining counters count successful state transitions.
/// </summary>
public readonly record struct PhysicsChainReadbackCounters(
    long Requested,
    long Coalesced,
    long Rejected,
    long Cancelled,
    long Expired,
    long Released,
    int LiveRequests);
