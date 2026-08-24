using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan.RenderGraph;

internal readonly record struct VulkanQueueOwnershipConfigCacheEntry(
    IReadOnlyCollection<RenderPassMetadata>? PassMetadata,
    VulkanBarrierPlanner.QueueOwnershipConfig Config);
