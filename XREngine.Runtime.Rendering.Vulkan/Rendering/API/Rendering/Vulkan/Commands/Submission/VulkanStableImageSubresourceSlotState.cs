namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One flat-directory entry. The synchronization lock publishes the state and
/// generation together, while the dictionary remains the cold lookup index.
/// </summary>
internal sealed class VulkanStableImageSubresourceSlotState
{
    internal ulong Generation;
    internal VulkanImageSubresourceState? State;
}
