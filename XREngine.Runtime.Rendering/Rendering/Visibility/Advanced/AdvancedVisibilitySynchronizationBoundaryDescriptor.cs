using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering;

/// <summary>
/// Vulkan stage/access states and matching OpenGL barrier for one visibility
/// sequence boundary.
/// </summary>
public readonly record struct AdvancedVisibilitySynchronizationBoundaryDescriptor(
    EAdvancedVisibilitySynchronizationBoundary Boundary,
    RenderGraphSyncState ProducerState,
    RenderGraphSyncState ConsumerState,
    EMemoryBarrierMask OpenGlBarrierMask);
