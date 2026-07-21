using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Superseded arena storage retained until its GPU retirement fence signals.
/// </summary>
internal readonly record struct DeferredPhysicsChainArenaResource(
    XRDataBuffer Resource,
    XRGpuFence? RetirementFence,
    long ByteLength);
