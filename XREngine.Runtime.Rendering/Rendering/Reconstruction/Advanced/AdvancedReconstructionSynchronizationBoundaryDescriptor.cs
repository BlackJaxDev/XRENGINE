using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering;

/// <summary>
/// Vulkan stage/access states and matching OpenGL barrier for reconstruction.
/// </summary>
public readonly record struct AdvancedReconstructionSynchronizationBoundaryDescriptor(
    EAdvancedReconstructionSynchronizationBoundary Boundary,
    RenderGraphSyncState ProducerState,
    RenderGraphSyncState ConsumerState,
    EMemoryBarrierMask OpenGlBarrierMask);
