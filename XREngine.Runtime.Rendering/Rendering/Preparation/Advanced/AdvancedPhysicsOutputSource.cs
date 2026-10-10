using XREngine.Rendering.Compute;

namespace XREngine.Rendering;

/// <summary>Identifies the physics page consumed by one aggregate output slot.</summary>
internal readonly record struct AdvancedPhysicsOutputSource(
    ulong ProducedFrameId,
    GPUPhysicsChainDispatcher? Dispatcher,
    PhysicsChainGpuOutputPageToken PageToken);
