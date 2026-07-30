namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Identifies one stable render-resource planner output while a window resize is in progress.
/// Extents are deliberately excluded so the first dimensions observed for this identity can be
/// retained until the resize ends.
/// </summary>
internal readonly record struct VulkanInteractiveResizePlannerContextKey(
    EVulkanFrameOpContextKind ContextKind,
    int PipelineIdentity,
    int ViewportIdentity,
    int OutputFrameBufferIdentity,
    int OutputTargetIdentity);
