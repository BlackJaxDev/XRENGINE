namespace XREngine.Rendering.Vulkan;

/// <summary>
/// ABA-safe identity for one tracked image subresource in the synchronization
/// state's flat sealed-submission directory.
/// </summary>
internal readonly record struct VulkanStableImageSubresourceSlotHandle(
    VulkanStableImageSubresourceIndex Index,
    ulong Generation)
{
    internal static VulkanStableImageSubresourceSlotHandle Invalid => default;

    internal bool IsValid => Index.IsValid && Generation != 0UL;
}
