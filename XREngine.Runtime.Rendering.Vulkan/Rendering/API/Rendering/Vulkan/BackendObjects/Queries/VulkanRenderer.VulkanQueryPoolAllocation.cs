using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct VulkanQueryPoolAllocation(
        QueryPool Pool,
        uint PoolIdentity,
        uint FirstQuery,
        uint QueryCount,
        uint Capacity,
        VulkanQueryPoolKey Key)
    {
        public bool IsValid => Pool.Handle != 0 && PoolIdentity != 0u && QueryCount != 0u;
    }
}
