using System.Net;
using System.Net.Sockets;

namespace XREngine.Networking;

/// <summary>Owns an IPv4 socket while exposing only the engine datagram contract.</summary>
internal sealed class SocketDatagramTransport : IDatagramTransport
{
    /// <summary>Bounds the per-socket endpoint caches so spoofed source addresses cannot grow them without limit.</summary>
    private const int MaxCachedEndpoints = 1024;

    private readonly UdpClient _client = new(AddressFamily.InterNetwork);
    private readonly SocketAddress _receiveAddress = new(AddressFamily.InterNetwork);
    private readonly Dictionary<SocketAddress, IPEndPoint> _receiveEndpoints = [];
    private readonly Dictionary<IPEndPoint, SocketAddress> _sendAddresses = [];
    private readonly object _sendAddressSync = new();

    public SocketDatagramTransport(string context) => UdpSocketOptions.DisableConnectionReset(_client, context);
    public int Available => _client.Available;
    public bool IsBound => _client.Client.IsBound;
    public bool Connected => _client.Client.Connected;
    public IPEndPoint? LocalEndPoint => _client.Client.LocalEndPoint as IPEndPoint;
    public bool ExclusiveAddressUse { get => _client.ExclusiveAddressUse; set => _client.ExclusiveAddressUse = value; }
    public bool MulticastLoopback { get => _client.MulticastLoopback; set => _client.MulticastLoopback = value; }
    public bool EnableBroadcast { get => _client.EnableBroadcast; set => _client.EnableBroadcast = value; }
    public bool ReuseAddress { set => _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, value); }
    public bool SupportsSpanDatagrams => true;
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

    public int Send(ReadOnlySpan<byte> buffer, IPEndPoint endpoint)
    {
        SocketAddress address = ResolveSendAddress(endpoint);
        try { return _client.Client.SendTo(buffer, SocketFlags.None, address); }
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

    /// <summary>
    /// Receives into caller storage using the socket-address overload, which reuses one address
    /// buffer instead of allocating an endpoint per datagram. Called only from the receive loop.
    /// </summary>
    public bool TryReceive(Span<byte> destination, out int bytesReceived, out IPEndPoint? remoteEndPoint)
    {
        bytesReceived = 0;
        remoteEndPoint = null;
        Socket socket = _client.Client;
        if (socket.Available <= 0)
            return false;

        try
        {
            bytesReceived = socket.ReceiveFrom(destination, SocketFlags.None, _receiveAddress);
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.MessageSize)
        {
            // The datagram exceeded the caller buffer and the stack discarded the remainder.
            bytesReceived = -1;
            remoteEndPoint = ResolveReceiveEndpoint();
            return true;
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.WouldBlock or SocketError.ConnectionReset)
        {
            bytesReceived = 0;
            return false;
        }
        catch (SocketException exception) { throw Wrap(exception); }

        remoteEndPoint = ResolveReceiveEndpoint();
        return true;
    }

    private IPEndPoint ResolveReceiveEndpoint()
    {
        if (_receiveEndpoints.TryGetValue(_receiveAddress, out IPEndPoint? cached))
            return cached;

        // First datagram from this address: copy the reusable buffer into an owned key.
        SocketAddress key = new(_receiveAddress.Family, _receiveAddress.Size);
        _receiveAddress.Buffer.Span[.._receiveAddress.Size].CopyTo(key.Buffer.Span);
        IPEndPoint endpoint = (IPEndPoint)new IPEndPoint(IPAddress.Any, 0).Create(key);
        if (_receiveEndpoints.Count >= MaxCachedEndpoints)
            _receiveEndpoints.Clear();
        _receiveEndpoints.Add(key, endpoint);
        return endpoint;
    }

    private SocketAddress ResolveSendAddress(IPEndPoint endpoint)
    {
        lock (_sendAddressSync)
        {
            if (_sendAddresses.TryGetValue(endpoint, out SocketAddress? cached))
                return cached;

            SocketAddress address = endpoint.Serialize();
            if (_sendAddresses.Count >= MaxCachedEndpoints)
                _sendAddresses.Clear();
            // Key on a private copy so a caller mutating its endpoint cannot corrupt the cache.
            _sendAddresses.Add(new IPEndPoint(endpoint.Address, endpoint.Port), address);
            return address;
        }
    }

    private static NetworkTransportException Wrap(SocketException exception)
        => new("Datagram transport failed.", exception.ErrorCode, exception);
}
