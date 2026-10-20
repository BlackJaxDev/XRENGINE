using XREngine.Components;

namespace XREngine.Rendering.Compute;

/// <summary>Identifies one renderer bounds slot in the committed chain layout.</summary>
public readonly record struct PhysicsChainGpuBoundsSource(
    GPUPhysicsChainDispatcher Dispatcher,
    XRMeshRenderer Renderer,
    uint BoundsSlot,
    uint SlotGeneration,
    long RendererBoneBufferGeneration);
