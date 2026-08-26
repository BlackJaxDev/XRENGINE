namespace XREngine.Components;

/// <summary>
/// Structural-boundary diagnostics for one world's content-addressed collider
/// set cache. Sets are retained for the world lifetime, so live and unique
/// counts intentionally match until an explicit eviction policy is added.
/// </summary>
public readonly record struct PhysicsChainColliderSetCacheSnapshot(
    int UniqueSetCount,
    int LiveSetCount,
    int TotalShapeCount,
    long EstimatedShapeBytes,
    long LookupCount,
    long DeduplicatedLookupCount);
