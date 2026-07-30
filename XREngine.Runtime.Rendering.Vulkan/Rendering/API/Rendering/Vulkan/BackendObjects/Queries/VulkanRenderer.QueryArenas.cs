namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private VulkanQueryPoolArenaManager? _queryPoolArenas;

    internal VulkanQueryPoolArenaManager QueryPoolArenas
        => _queryPoolArenas ??= new VulkanQueryPoolArenaManager(this);

    internal QueryArenaTelemetry VulkanQueryArenaStats
        => _queryPoolArenas?.CaptureTelemetry() ?? default;

    private void DestroyVulkanQueryArenas()
    {
        _queryPoolArenas?.Dispose();
        _queryPoolArenas = null;
    }
}
