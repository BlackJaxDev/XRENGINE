using System.Net;

namespace XREngine.Networking;

/// <summary>Creates explicitly installed platform transports; unsupported native operations fail explicitly.</summary>
public interface INetworkTransportBackend
{
    IDatagramTransport CreateDatagram(string diagnosticContext);
    bool IsNetworkAvailable();
    string[] GetLocalIPv4(int interfaceType);
    Task<Stream> ConnectStreamAsync(string host, int port, CancellationToken cancellationToken = default);
    Task<Stream> AcceptStreamAsync(int port, CancellationToken cancellationToken = default);
    Task<IRealtimeTlsTunnel> ConnectTlsTunnelAsync(IPAddress address, int port, string serverName,
        string? developmentCertificateSha256, CancellationToken cancellationToken);
}
