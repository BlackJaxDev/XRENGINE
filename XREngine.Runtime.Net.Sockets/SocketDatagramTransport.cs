using System.Net;
using System.Net.Sockets;

namespace XREngine.Networking;

/// <summary>Owns an IPv4 socket while exposing only the engine datagram contract.</summary>
internal sealed class SocketDatagramTransport : IDatagramTransport
{
    private readonly UdpClient _client = new(AddressFamily.InterNetwork);

    public SocketDatagramTransport(string context) => UdpSocketOptions.DisableConnectionReset(_client, context);
    public int Available => _client.Available;
    public bool IsBound => _client.Client.IsBound;
    public bool Connected => _client.Client.Connected;
    public IPEndPoint? LocalEndPoint => _client.Client.LocalEndPoint as IPEndPoint;
    public bool ExclusiveAddressUse { get => _client.ExclusiveAddressUse; set => _client.ExclusiveAddressUse = value; }
    public bool MulticastLoopback { get => _client.MulticastLoopback; set => _client.MulticastLoopback = value; }
    public bool EnableBroadcast { get => _client.EnableBroadcast; set => _client.EnableBroadcast = value; }
    public bool ReuseAddress { set => _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, value); }
    public void Bind(IPEndPoint endpoint) => _client.Client.Bind(endpoint);
    public void Connect(IPEndPoint endpoint) => _client.Connect(endpoint);
    public void JoinMulticastGroup(IPAddress address) => _client.JoinMulticastGroup(address);
    public void Close() => _client.Close();
    public void Dispose() => _client.Dispose();

    public int Send(byte[] buffer, int count, IPEndPoint endpoint)
    {
        try { return _client.Send(buffer, count, endpoint); }
        catch (SocketException exception) { throw Wrap(exception); }
    }

    public async Task<int> SendAsync(byte[] buffer, int count, IPEndPoint endpoint)
    {
        try { return await _client.SendAsync(buffer, count, endpoint).ConfigureAwait(false); }
        catch (SocketException exception) { throw Wrap(exception); }
    }

    public async ValueTask<DatagramReceiveResult> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            UdpReceiveResult result = await _client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            return new(result.Buffer, result.RemoteEndPoint);
        }
        catch (SocketException exception) { throw Wrap(exception); }
    }

    private static NetworkTransportException Wrap(SocketException exception)
        => new("Datagram transport failed.", exception.ErrorCode, exception);
}
