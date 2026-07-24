using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Superseded arena storage retained until its GPU retirement fence signals.
/// </summary>
internal readonly record struct DeferredPhysicsChainArenaResource(
    XRDataBuffer Resource,
    IPhysicsChainComputeBackend Backend,
    XRGpuFence? RetirementFence,
    long ByteLength,
    int FailedFenceRetryCount = 0);
