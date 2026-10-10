using System.Net;

namespace XREngine.Networking;

/// <summary>Browser-capable realtime transport. Native sockets and interface discovery are explicitly unavailable.</summary>
public sealed class WebSocketNetworkTransportBackend : INetworkTransportBackend, IRealtimeWebSocketBackend
{
    public Task<IRealtimeWebSocketTransport> ConnectWebSocketAsync(Uri endpoint, IPEndPoint protocolPeer,
        CancellationToken cancellationToken = default)
        => WebSocketDatagramTransport.ConnectAsync(endpoint, protocolPeer, cancellationToken);

    public IDatagramTransport CreateDatagram(string diagnosticContext)
        => throw new NotSupportedException("Browser raw datagrams are unavailable; use an asynchronous realtime WebSocket connection.");
    public bool IsNetworkAvailable()
        => throw new NotSupportedException("Browser connectivity is established by the realtime connection, not native interface discovery.");
    public string[] GetLocalIPv4(int interfaceType)
        => throw new NotSupportedException("Browser local network interface discovery is unavailable.");
    public Task<Stream> ConnectStreamAsync(string host, int port, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Browser raw TCP streams are unavailable.");
    public Task<Stream> AcceptStreamAsync(int port, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Browser listening sockets are unavailable.");
    public Task<IRealtimeTlsTunnel> ConnectTlsTunnelAsync(IPAddress address, int port, string serverName,
        string? developmentCertificateSha256, CancellationToken cancellationToken)
        => throw new NotSupportedException("Browser raw TLS tunnels and certificate pin overrides are unavailable; use wss.");
}
