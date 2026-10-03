using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace XREngine.Networking;

/// <summary>Creates socket-owned datagram, stream, and authenticated realtime transports.</summary>
internal sealed class SocketNetworkTransportBackend : INetworkTransportBackend
{
    public IDatagramTransport CreateDatagram(string diagnosticContext) => new SocketDatagramTransport(diagnosticContext);
    public bool IsNetworkAvailable() => NetworkInterface.GetIsNetworkAvailable();

    public string[] GetLocalIPv4(int interfaceType)
    {
        List<string> addresses = [];
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if ((int)adapter.NetworkInterfaceType != interfaceType || adapter.OperationalStatus != OperationalStatus.Up)
                continue;
            foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
                if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                    addresses.Add(address.Address.ToString());
        }
        return addresses.ToArray();
    }

    public async Task<Stream> ConnectStreamAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        TcpClient client = new();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new SocketOwnedStream(client);
        }
        catch { client.Dispose(); throw; }
    }

    public async Task<Stream> AcceptStreamAsync(int port, CancellationToken cancellationToken = default)
    {
        using TcpListener listener = new(IPAddress.Any, port);
        listener.Start();
        TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        try { return new SocketOwnedStream(client); }
        catch { client.Dispose(); throw; }
    }

    public async Task<IRealtimeTlsTunnel> ConnectTlsTunnelAsync(IPAddress address, int port, string serverName,
        string? developmentCertificateSha256, CancellationToken cancellationToken)
        => await NativeRealtimeTlsClientTunnel.ConnectAsync(address, port, serverName,
            developmentCertificateSha256, cancellationToken).ConfigureAwait(false);
}
