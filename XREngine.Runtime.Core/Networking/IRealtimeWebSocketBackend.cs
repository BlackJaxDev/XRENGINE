using System.Net;

namespace XREngine.Networking;

/// <summary>Opens an asynchronous framed connection without requiring a local datagram socket.</summary>
public interface IRealtimeWebSocketBackend
{
    Task<IRealtimeWebSocketTransport> ConnectWebSocketAsync(Uri endpoint, IPEndPoint protocolPeer,
        CancellationToken cancellationToken = default);
}
