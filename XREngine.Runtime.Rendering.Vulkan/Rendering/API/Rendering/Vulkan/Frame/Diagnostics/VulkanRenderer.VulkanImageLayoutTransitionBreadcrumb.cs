using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanImageLayoutTransitionBreadcrumb(
    ulong Serial,
    ulong CommandBufferHandle,
    ulong ImageHandle,
    ImageAspectFlags AspectMask,
    uint BaseMipLevel,
    uint LevelCount,
    uint BaseArrayLayer,
    uint LayerCount,
    ImageLayout OldLayout,
    ImageLayout NewLayout,
    uint SourceQueueFamily,
    uint DestinationQueueFamily,
    string? Caller);
