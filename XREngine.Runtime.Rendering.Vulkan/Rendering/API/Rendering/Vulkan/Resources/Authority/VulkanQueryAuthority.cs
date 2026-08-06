namespace XREngine.Rendering.Vulkan;

/// <summary>Owns registrations for specialized Vulkan query providers.</summary>
internal sealed class VulkanQueryAuthority
{
    private readonly object _sync = new();
    private readonly Dictionary<ERenderQueryKind, IVulkanSpecializedQueryProvider> _providers = [];
    internal bool OcclusionPreciseAdvertised;
    internal bool OcclusionPreciseEnabled;
    internal bool PipelineStatisticsAdvertised;
    internal bool PipelineStatisticsEnabled;
    internal bool InheritedQueriesAdvertised;
    internal bool InheritedQueriesEnabled;
    internal bool HostResetAdvertised;
    internal bool MeshShaderQueriesEnabled;
    internal bool PrimitivesGeneratedAdvertised;
    internal bool PrimitivesGeneratedEnabled;
    internal bool PrimitivesGeneratedNonZeroStreamsEnabled;
    internal VulkanQueryCapabilities Capabilities = VulkanQueryCapabilities.Unsupported;
    internal VulkanQueryPoolArenaManager? Arenas;

    internal void Register(IVulkanSpecializedQueryProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_sync)
            _providers[provider.Kind] = provider;
    }

    internal void Unregister(IVulkanSpecializedQueryProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_sync)
        {
            if (_providers.TryGetValue(provider.Kind, out IVulkanSpecializedQueryProvider? registered) &&
                ReferenceEquals(registered, provider))
            {
                _providers.Remove(provider.Kind);
            }
        }
    }

    internal bool TryGet(ERenderQueryKind kind, out IVulkanSpecializedQueryProvider provider)
    {
        lock (_sync)
            return _providers.TryGetValue(kind, out provider!);
    }
}
