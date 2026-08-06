namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Output-runtime registry for detached ImGui platform-window WSI lifetimes.
/// It intentionally tracks output objects only; native window and input
/// lifetime remain owned by the UI adapter.
/// </summary>
internal sealed class VulkanImGuiPlatformWindowOutputAuthority
{
    private readonly object _gate = new();
    private readonly HashSet<VulkanImGuiPlatformWindowOutputLifetime> _active = [];

    internal void Register(VulkanImGuiPlatformWindowOutputLifetime lifetime)
    {
        lock (_gate)
            _active.Add(lifetime);
    }

    internal void Unregister(VulkanImGuiPlatformWindowOutputLifetime lifetime)
    {
        lock (_gate)
            _active.Remove(lifetime);
    }

    internal int ActiveLifetimeCount
    {
        get
        {
            lock (_gate)
                return _active.Count;
        }
    }
}
