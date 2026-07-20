namespace XREngine.Networking;

/// <summary>
/// Host operations required to configure networking from a discovery announcement.
/// </summary>
public interface IRuntimeNetworkDiscoveryHostServices
{
    /// <summary>
    /// Applies discovery connection settings and returns the host networking manager, if enabled.
    /// </summary>
    object? ConfigureNetworking(NetworkDiscoveryConnectionSettings settings);
}

/// <summary>
/// Process-wide host boundary used by LAN discovery.
/// </summary>
public static class RuntimeNetworkDiscoveryHostServices
{
    private sealed class UnconfiguredRuntimeNetworkDiscoveryHostServices : IRuntimeNetworkDiscoveryHostServices
    {
        public object? ConfigureNetworking(NetworkDiscoveryConnectionSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            throw new InvalidOperationException("Runtime network discovery host services have not been configured.");
        }
    }

    private static IRuntimeNetworkDiscoveryHostServices _current = new UnconfiguredRuntimeNetworkDiscoveryHostServices();

    public static IRuntimeNetworkDiscoveryHostServices Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }
}