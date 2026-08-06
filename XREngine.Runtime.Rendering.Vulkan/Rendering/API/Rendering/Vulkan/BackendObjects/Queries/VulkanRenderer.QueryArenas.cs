using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer : IVulkanQueryArenaFacility
{
    private ref VulkanQueryPoolArenaManager? _queryPoolArenas => ref ResourceRuntime.Queries.Arenas;

    internal VulkanQueryPoolArenaManager QueryPoolArenas
        => _queryPoolArenas ??= new VulkanQueryPoolArenaManager(this);

    internal QueryArenaTelemetry VulkanQueryArenaStats
        => _queryPoolArenas?.CaptureTelemetry() ?? default;

    private void DestroyVulkanQueryArenas()
    {
        _queryPoolArenas?.Dispose();
        _queryPoolArenas = null;
    }

    bool IVulkanQueryArenaFacility.IsDeviceLost => IsDeviceLost;
    bool IVulkanQueryArenaFacility.IsLogicalDeviceReady => _deviceContext.IsReady;

    Result IVulkanQueryArenaFacility.CreateQueryPool(ref QueryPoolCreateInfo createInfo, out QueryPool pool)
        => VulkanApi.CreateQueryPool(Device, ref createInfo, null, out pool);

    void IVulkanQueryArenaFacility.RegisterQueryPool(QueryPool pool, string owner)
        => RegisterVulkanResource(ObjectType.QueryPool, pool.Handle, owner);

    void IVulkanQueryArenaFacility.RetireQueryPool(QueryPool pool)
        => RetireQueryPool(pool);
}
