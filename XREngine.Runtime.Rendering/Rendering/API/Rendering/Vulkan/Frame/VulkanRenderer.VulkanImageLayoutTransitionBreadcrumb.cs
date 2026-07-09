using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct VulkanImageLayoutTransitionBreadcrumb(
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
}
