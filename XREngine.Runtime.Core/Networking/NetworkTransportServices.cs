namespace XREngine.Networking;

/// <summary>Stores the host's statically installed transport backend.</summary>
public static class NetworkTransportServices
{
    private static INetworkTransportBackend? _current;
    public static INetworkTransportBackend? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
    public static INetworkTransportBackend Required => Current ??
        throw new NotSupportedException("Network transports are not installed. Install a transport backend in this host.");
}
