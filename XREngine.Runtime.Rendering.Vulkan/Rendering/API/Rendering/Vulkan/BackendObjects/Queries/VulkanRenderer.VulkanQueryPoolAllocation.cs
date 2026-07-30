using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanQueryPoolAllocation(
    QueryPool Pool,
    uint PoolIdentity,
    uint FirstQuery,
    uint QueryCount,
    uint Capacity,
    VulkanQueryPoolKey Key)
{
    public bool IsValid => Pool.Handle != 0 && PoolIdentity != 0u && QueryCount != 0u;
}
