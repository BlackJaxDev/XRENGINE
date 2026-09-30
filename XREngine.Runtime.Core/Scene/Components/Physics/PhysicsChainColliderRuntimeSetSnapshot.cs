namespace XREngine.Components;

/// <summary>Diagnostics for one world-owned collider pose stream and BVH.</summary>
public readonly record struct PhysicsChainColliderRuntimeSetSnapshot(
    long ColliderSetId,
    uint ShapeVersion,
    uint PoseVersion,
    int ColliderCount,
    int BvhNodeCount,
    long RefitCount,
    long QueryCount,
    long CandidateOverflowCount,
    long TraversalFailureCount);
