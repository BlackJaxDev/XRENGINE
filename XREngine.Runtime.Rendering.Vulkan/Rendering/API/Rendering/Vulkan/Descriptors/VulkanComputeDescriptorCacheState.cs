namespace XREngine.Rendering.Vulkan;

/// <summary>Owns compute descriptor caches for one logical-device lifetime.</summary>
internal sealed class VulkanComputeDescriptorCacheState
{
    internal object Gate { get; } = new();
    internal ComputeDescriptorImageCache[]? Caches;
}
