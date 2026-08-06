using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed class ComputeDescriptorImageCache
{
    public Dictionary<ComputeDescriptorCacheKey, DescriptorSet[]> CachedSets { get; } = new();
    public Dictionary<ulong, List<ComputeDescriptorPoolBlock>> PoolsBySchema { get; } = new();
}
