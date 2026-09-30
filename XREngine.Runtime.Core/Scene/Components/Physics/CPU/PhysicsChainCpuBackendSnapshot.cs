namespace XREngine.Components;

/// <summary>
/// Delayed-friendly capacity and live-use diagnostics for the CPU backend.
/// </summary>
public readonly record struct PhysicsChainCpuBackendSnapshot(
    int InstanceCapacity,
    int LiveInstanceCount,
    int InstanceGrowthCount,
    int LiveParticleCount,
    int LiveColliderCount,
    int SharedColliderReferenceCount,
    long SharedColliderQueryCount,
    long SharedColliderFullSetFallbackCount,
    long SharedColliderBatchGroupCount,
    long SharedColliderGroupedInstanceCount);
