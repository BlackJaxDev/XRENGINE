namespace XREngine.Components;

/// <summary>Counts full and cached GPU rest input captures in one physics world.</summary>
public readonly record struct PhysicsChainRigidGpuRestInputCacheDiagnostics(
    long Hits, long Misses, int BlockedRanges);
