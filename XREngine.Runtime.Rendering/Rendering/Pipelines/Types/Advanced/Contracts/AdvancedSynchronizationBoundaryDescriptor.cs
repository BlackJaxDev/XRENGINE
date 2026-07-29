using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering;

/// <summary>
/// Logical Vulkan state transition plus the OpenGL barrier mask for one frame boundary.
/// A null consumer stage denotes the presentation engine.
/// </summary>
public readonly record struct AdvancedSynchronizationBoundaryDescriptor(
    EAdvancedSynchronizationBoundary Boundary,
    EAdvancedRenderStage ProducerStage,
    EAdvancedRenderStage? ConsumerStage,
    RenderGraphSyncState ProducerState,
    RenderGraphSyncState ConsumerState,
    EAdvancedOpenGlMemoryBarrier OpenGlBarrierMask);
