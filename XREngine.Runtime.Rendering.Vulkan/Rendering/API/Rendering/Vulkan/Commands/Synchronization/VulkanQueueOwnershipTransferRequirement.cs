using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable queue-family ownership requirement captured from an image barrier.
/// </summary>
internal readonly record struct VulkanQueueOwnershipTransferRequirement(
    ulong ImageHandle,
    ImageSubresourceRange Range,
    ImageLayout OldLayout,
    ImageLayout NewLayout,
    uint SourceQueueFamilyIndex,
    uint DestinationQueueFamilyIndex,
    PipelineStageFlags2 SourceStageMask,
    AccessFlags2 SourceAccessMask,
    PipelineStageFlags2 DestinationStageMask,
    AccessFlags2 DestinationAccessMask,
    ulong ResourceGeneration)
{
    public bool IsValid =>
        ImageHandle != 0 &&
        SourceQueueFamilyIndex != Vk.QueueFamilyIgnored &&
        DestinationQueueFamilyIndex != Vk.QueueFamilyIgnored &&
        SourceQueueFamilyIndex != DestinationQueueFamilyIndex;

    public EVulkanQueueOwnershipTransferRole ResolveRole(uint submissionQueueFamilyIndex)
        => submissionQueueFamilyIndex == SourceQueueFamilyIndex
            ? EVulkanQueueOwnershipTransferRole.Release
            : submissionQueueFamilyIndex == DestinationQueueFamilyIndex
                ? EVulkanQueueOwnershipTransferRole.Acquire
                : EVulkanQueueOwnershipTransferRole.Invalid;

    public bool Contains(
        ulong imageHandle,
        uint mipLevel,
        uint arrayLayer,
        ImageAspectFlags aspect)
        => ImageHandle == imageHandle &&
           ContainsIndex(
               mipLevel,
               Range.BaseMipLevel,
               Range.LevelCount) &&
           ContainsIndex(
               arrayLayer,
               Range.BaseArrayLayer,
               Range.LayerCount) &&
           (Range.AspectMask & aspect) != 0;

    public bool IsPairedWith(
        in VulkanQueueOwnershipTransferRequirement other,
        ulong imageHandle,
        uint mipLevel,
        uint arrayLayer,
        ImageAspectFlags aspect)
        => Contains(imageHandle, mipLevel, arrayLayer, aspect) &&
           other.Contains(imageHandle, mipLevel, arrayLayer, aspect) &&
           SourceQueueFamilyIndex == other.SourceQueueFamilyIndex &&
           DestinationQueueFamilyIndex == other.DestinationQueueFamilyIndex &&
           OldLayout == other.OldLayout &&
           NewLayout == other.NewLayout &&
           (ResourceGeneration == 0 ||
            other.ResourceGeneration == 0 ||
            ResourceGeneration == other.ResourceGeneration);

    private static bool ContainsIndex(
        uint value,
        uint baseIndex,
        uint count)
        => value >= baseIndex &&
           value - baseIndex < Math.Max(count, 1u);
}
