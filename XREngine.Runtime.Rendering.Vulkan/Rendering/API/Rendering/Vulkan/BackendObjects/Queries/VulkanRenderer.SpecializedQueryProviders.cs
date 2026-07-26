namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly object _specializedQueryProviderLock = new();
    private readonly Dictionary<ERenderQueryKind, IVulkanSpecializedQueryProvider> _specializedQueryProviders = [];

    public void RegisterSpecializedQueryProvider(IVulkanSpecializedQueryProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_specializedQueryProviderLock)
            _specializedQueryProviders[provider.Kind] = provider;
    }

    public void UnregisterSpecializedQueryProvider(IVulkanSpecializedQueryProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_specializedQueryProviderLock)
        {
            if (_specializedQueryProviders.TryGetValue(provider.Kind, out IVulkanSpecializedQueryProvider? registered) &&
                ReferenceEquals(registered, provider))
            {
                _specializedQueryProviders.Remove(provider.Kind);
            }
        }
    }

    private bool TryGetSpecializedQueryProvider(
        ERenderQueryKind kind,
        out IVulkanSpecializedQueryProvider provider)
    {
        lock (_specializedQueryProviderLock)
            return _specializedQueryProviders.TryGetValue(kind, out provider!);
    }
}
