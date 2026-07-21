namespace XREngine.Components;

public readonly record struct PhysicsChainCpuSharedColliderSetSnapshot(
    long ColliderSetId,
    int ColliderCount,
    long QueryCount,
    long SmallSetBypassCount,
    long CandidateCount,
    long FullSetFallbackCount,
    PhysicsChainColliderRuntimeSetSnapshot Broadphase);
