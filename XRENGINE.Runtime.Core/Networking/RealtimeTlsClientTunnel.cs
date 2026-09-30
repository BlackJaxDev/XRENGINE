using System.Net;

namespace XREngine.Networking;

/// <summary>Provides the authenticated realtime tunnel selected by the host's installed transport.</summary>
public sealed class RealtimeTlsClientTunnel : IDisposable
{
    private readonly IRealtimeTlsTunnel _transport;
    private RealtimeTlsClientTunnel(IRealtimeTlsTunnel transport) => _transport = transport;
    public IPEndPoint LocalEndpoint => _transport.LocalEndpoint;
    public string? Failure => _transport.Failure;

    public static async Task<RealtimeTlsClientTunnel> ConnectAsync(IPAddress address, int port, string serverName,
        string? developmentCertificateSha256 = null, CancellationToken cancellationToken = default)
        => new(await NetworkTransportServices.Required.ConnectTlsTunnelAsync(address, port, serverName,
            developmentCertificateSha256, cancellationToken).ConfigureAwait(false));

    public void Start(IPEndPoint engineEndpoint) => _transport.Start(engineEndpoint);
    public void Dispose() => _transport.Dispose();
}
