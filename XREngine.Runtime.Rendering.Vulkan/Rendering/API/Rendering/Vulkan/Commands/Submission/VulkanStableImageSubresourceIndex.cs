namespace XREngine.Rendering.Vulkan;

/// <summary>Stable flat-directory position for a tracked image subresource.</summary>
internal readonly record struct VulkanStableImageSubresourceIndex(uint Value)
{
    internal bool IsValid => Value != 0u;
}
