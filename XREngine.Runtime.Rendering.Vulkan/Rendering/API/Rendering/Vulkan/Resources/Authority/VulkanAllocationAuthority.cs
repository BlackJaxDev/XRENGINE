namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns allocation registries and staging-pool state for a Vulkan device lifetime.
/// </summary>
/// <remarks>
/// Allocation and destruction algorithms still live in renderer partials because they require
/// device-state validation and lifetime transactions. Those partials must access the registries
/// through this authority rather than introduce parallel handle maps.
/// </remarks>
internal sealed class VulkanAllocationAuthority(
    VulkanBufferResourceManager buffers,
    VulkanImageAllocationTracker images,
    VulkanStagingManager staging)
{
    internal VulkanBufferResourceManager Buffers { get; } =
        buffers ?? throw new ArgumentNullException(nameof(buffers));
    internal VulkanImageAllocationTracker Images { get; } =
        images ?? throw new ArgumentNullException(nameof(images));
    internal VulkanStagingManager Staging { get; } =
        staging ?? throw new ArgumentNullException(nameof(staging));
}
