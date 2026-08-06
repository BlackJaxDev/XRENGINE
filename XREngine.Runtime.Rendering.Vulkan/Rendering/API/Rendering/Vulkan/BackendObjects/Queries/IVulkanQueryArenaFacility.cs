using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Native device and retirement operations required by a query-pool arena.</summary>
internal unsafe interface IVulkanQueryArenaFacility
{
    bool IsDeviceLost { get; }
    bool IsLogicalDeviceReady { get; }
    Result CreateQueryPool(ref QueryPoolCreateInfo createInfo, out QueryPool pool);
    void RegisterQueryPool(QueryPool pool, string owner);
    void RetireQueryPool(QueryPool pool);
}
