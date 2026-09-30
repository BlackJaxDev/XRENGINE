using System.Net;

namespace XREngine.Networking;

/// <summary>Exposes an authenticated encrypted transport bridge without platform TLS or socket types.</summary>
public interface IRealtimeTlsTunnel : IDisposable
{
    IPEndPoint LocalEndpoint { get; }
    string? Failure { get; }
    void Start(IPEndPoint engineEndpoint);
}
