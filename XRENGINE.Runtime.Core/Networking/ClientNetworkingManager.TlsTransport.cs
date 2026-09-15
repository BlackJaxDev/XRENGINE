using System.Net;
using XREngine.Networking;

namespace XREngine;

public partial class ClientNetworkingManager
{
    private RealtimeTlsClientTunnel? _tlsTunnel;

    /// <summary>Selected by the trusted managed handoff. Unsupported transports never fall back to UDP.</summary>
    public RealtimeTransportKind Transport { get; set; }
    /// <summary>Original advertised name used for certificate identity checks, before DNS resolution.</summary>
    public string? TlsServerName { get; set; }
    /// <summary>Explicit local developer trust only; SHA-256 of SubjectPublicKeyInfo, never a remote handoff override.</summary>
    public string? DevelopmentTlsCertificatePin { get; set; }
    public string? EncryptedTransportFailure => _tlsTunnel?.Failure;

    private void StartSelectedTransport(IPAddress serverAddress, int serverPort, int clientPort)
    {
        if (Transport == RealtimeTransportKind.NativeUdp)
        {
            StartUdpSender(serverAddress, serverPort, clientPort);
            return;
        }
        if (Transport != RealtimeTransportKind.NativeTls || !IsManagedTransportRequested)
            throw new NotSupportedException("Encrypted realtime requires a managed player admission.");
        try
        {
            _tlsTunnel = RealtimeTlsClientTunnel.ConnectAsync(serverAddress, serverPort, TlsServerName ?? serverAddress.ToString(),
                DevelopmentTlsCertificatePin).GetAwaiter().GetResult();
            IPEndPoint local = _tlsTunnel.LocalEndpoint;
            StartUdpSender(local.Address, local.Port, clientPort);
            _tlsTunnel.Start(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)UdpSender!.Client.LocalEndPoint!).Port));
        }
        catch
        {
            _tlsTunnel?.Dispose();
            _tlsTunnel = null;
            throw;
        }
    }
}
