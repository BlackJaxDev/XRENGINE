namespace XREngine.Rendering.Vulkan.RenderGraph;

internal readonly record struct VulkanFrameOpPlannerStateKey(
    EVulkanFrameOpContextKind ContextKind,
    int PipelineIdentity,
    int ViewportIdentity,
    uint DisplayWidth,
    uint DisplayHeight,
    uint InternalWidth,
    uint InternalHeight,
    int OutputFrameBufferIdentity,
    int OutputTargetIdentity,
    ulong LogicalViewId,
    int ResourceRegistrySignature,
    int PassMetadataSignature,
    ulong ResourceGeneration,
    ulong DescriptorGeneration,
    uint SubmissionQueueFamily);
