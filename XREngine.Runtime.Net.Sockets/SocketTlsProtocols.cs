using System.Net.Security;

namespace XREngine.Networking;

/// <summary>Defines the application protocol negotiated by native realtime TLS peers.</summary>
internal static class SocketTlsProtocols
{
    public static readonly SslApplicationProtocol Realtime = new(RealtimeTlsFraming.ProtocolName);
}
