namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Coordinator-owned cache for topology-only bin manifests. A cached manifest
/// never crosses a membership topology generation, so workers cannot observe a
/// stale native resource declaration after replacement or eviction.
/// </summary>
internal sealed class VulkanStableBinManifestCache
{
    private readonly Dictionary<VulkanRenderBinKey, VulkanBinResourceManifest> _manifests = [];
    private ulong _topologyGeneration;

    internal void InvalidateForTopology(ulong topologyGeneration)
    {
        if (_topologyGeneration == topologyGeneration)
            return;
        _manifests.Clear();
        _topologyGeneration = topologyGeneration;
    }

    internal bool TryGet(
        ulong topologyGeneration,
        in VulkanRenderBinKey key,
        out VulkanBinResourceManifest? manifest)
    {
        InvalidateForTopology(topologyGeneration);
        return _manifests.TryGetValue(key, out manifest);
    }

    internal void Store(
        ulong topologyGeneration,
        in VulkanRenderBinKey key,
        VulkanBinResourceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        InvalidateForTopology(topologyGeneration);
        _manifests[key] = manifest;
    }

    internal void Clear()
    {
        _manifests.Clear();
        _topologyGeneration = 0u;
    }
}
