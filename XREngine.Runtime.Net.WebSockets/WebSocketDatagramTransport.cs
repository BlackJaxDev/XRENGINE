using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace XREngine.Networking;

/// <summary>Preserves each authenticated realtime datagram as one bounded binary WebSocket message.</summary>
internal sealed class WebSocketDatagramTransport : IRealtimeWebSocketTransport
{
    private readonly ClientWebSocket _connection;
    private readonly IPEndPoint _peer;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _outbound = CreateQueue();
    private readonly Channel<byte[]> _inbound = CreateQueue();
    private readonly object _queueLock = new();
    private int _outboundBytes;
    private int _inboundBytes;
    private int _disposed;
    private string? _failure;

    private WebSocketDatagramTransport(ClientWebSocket connection, IPEndPoint peer)
    {
        _connection = connection;
        _peer = new IPEndPoint(peer.Address, peer.Port);
    }

    private static Channel<byte[]> CreateQueue() => Channel.CreateBounded<byte[]>(new BoundedChannelOptions(RealtimeWebSocketProtocol.MaximumQueuedDatagrams)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });

    public string? Failure => Volatile.Read(ref _failure);
    public int Available => _inbound.Reader.Count;
    public bool IsBound => false;
    public bool Connected => Volatile.Read(ref _disposed) == 0 && _connection.State == WebSocketState.Open;
    public IPEndPoint? LocalEndPoint => null;
    public bool ExclusiveAddressUse { get => throw Unsupported(); set => throw Unsupported(); }
    public bool MulticastLoopback { get => throw Unsupported(); set => throw Unsupported(); }
    public bool EnableBroadcast { get => throw Unsupported(); set => throw Unsupported(); }
    public bool ReuseAddress { set => throw Unsupported(); }
    public void Bind(IPEndPoint endpoint) => throw Unsupported();
    public void Connect(IPEndPoint endpoint) => throw Unsupported();
    public void JoinMulticastGroup(IPAddress address) => throw Unsupported();
    private static NotSupportedException Unsupported() => new("A realtime WebSocket has no local datagram socket, multicast, broadcast, or bind options.");

    public static async Task<IRealtimeWebSocketTransport> ConnectAsync(Uri endpoint, IPEndPoint protocolPeer, CancellationToken cancellationToken)
    {
        RealtimeWebSocketProtocol.ValidateEndpoint(endpoint);
        ArgumentNullException.ThrowIfNull(protocolPeer);
        ClientWebSocket connection = new();
        connection.Options.AddSubProtocol(RealtimeWebSocketProtocol.Subprotocol);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await connection.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);
            if (connection.SubProtocol != RealtimeWebSocketProtocol.Subprotocol)
                throw new InvalidDataException(RealtimeWireProtocol.UpdateRequiredMessage);
            WebSocketDatagramTransport transport = new(connection, protocolPeer);
            _ = transport.PumpAsync();
            return transport;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public int Send(byte[] buffer, int count, IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!Connected)
            throw new IOException(Failure ?? "Realtime WebSocket is closed.");
        if (!endpoint.Equals(_peer) || count is < 1 or > RealtimeWebSocketProtocol.MaximumDatagramBytes || count > buffer.Length
            || !ManagedUdpEnvelope.TryRead(buffer.AsSpan(0, count), out var header, out _, out _)
            || header.Direction != ManagedUdpDirection.ClientToServer)
            throw new InvalidDataException("Realtime WebSocket requires a managed client datagram for its immutable peer.");
        lock (_queueLock)
        {
            if (_outboundBytes + count > RealtimeWebSocketProtocol.MaximumQueuedBytes
                || _outbound.Reader.Count >= RealtimeWebSocketProtocol.MaximumQueuedDatagrams)
            {
                Fail("Realtime WebSocket send backlog exceeded; fresh admission and state resynchronization are required.");
                throw new IOException(Failure);
            }
            // The caller owns its buffer and may reuse it after acceptance.
            byte[] owned = buffer.AsSpan(0, count).ToArray();
            if (!_outbound.Writer.TryWrite(owned))
                throw new IOException("Realtime WebSocket is closed.");
            _outboundBytes += count;
        }
        return count;
    }

    public Task<int> SendAsync(byte[] buffer, int count, IPEndPoint endpoint)
        => Task.FromResult(Send(buffer, count, endpoint));

    public async ValueTask<DatagramReceiveResult> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        byte[] bytes = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        lock (_queueLock)
            _inboundBytes = Math.Max(0, _inboundBytes - bytes.Length);
        return new DatagramReceiveResult(bytes, _peer);
    }

    private async Task PumpAsync()
    {
        try
        {
            Task send = SendLoopAsync();
            Task receive = ReceiveLoopAsync();
            await Task.WhenAny(send, receive).ConfigureAwait(false);
            _stop.Cancel();
            _connection.Abort();
            await Task.WhenAll(send, receive).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException or ChannelClosedException)
        {
            if (Volatile.Read(ref _disposed) == 0)
                Interlocked.CompareExchange(ref _failure, "Realtime WebSocket disconnected; obtain fresh admission and resynchronize before continuing.", null);
        }
        finally { Dispose(); }
    }

    private async Task SendLoopAsync()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        while (await _outbound.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
        {
            if (!_outbound.Reader.TryRead(out byte[]? bytes))
                continue;
            lock (_queueLock)
                _outboundBytes = Math.Max(0, _outboundBytes - bytes.Length);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await _connection.SendAsync(bytes.AsMemory(), WebSocketMessageType.Binary, true, deadline.Token).ConfigureAwait(false);
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[RealtimeWebSocketProtocol.MaximumDatagramBytes];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        while (!_stop.IsCancellationRequested)
        {
            int count = 0;
            int fragments = 0;
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            ValueWebSocketReceiveResult result;
            do
            {
                if (count == buffer.Length || ++fragments > 256)
                    throw new InvalidDataException("Realtime WebSocket message exceeds the datagram limit.");
                result = await _connection.ReceiveAsync(buffer.AsMemory(count), deadline.Token).ConfigureAwait(false);
                if (result.MessageType != WebSocketMessageType.Binary)
                    throw new InvalidDataException("Realtime WebSocket requires binary datagrams.");
                count += result.Count;
            } while (!result.EndOfMessage);
            if (!ManagedUdpEnvelope.TryRead(buffer.AsSpan(0, count), out var header, out _, out _)
                || header.Direction != ManagedUdpDirection.ServerToClient)
                throw new InvalidDataException("Realtime WebSocket received an invalid managed server datagram.");
            lock (_queueLock)
            {
                if (_inboundBytes + count > RealtimeWebSocketProtocol.MaximumQueuedBytes
                    || _inbound.Reader.Count >= RealtimeWebSocketProtocol.MaximumQueuedDatagrams)
                    throw new InvalidDataException("Realtime WebSocket receive backlog exceeded.");
                if (!_inbound.Writer.TryWrite(buffer.AsSpan(0, count).ToArray()))
                    throw new IOException("Realtime WebSocket is closed.");
                _inboundBytes += count;
            }
        }
    }

    private void Fail(string reason)
    {
        Interlocked.CompareExchange(ref _failure, reason, null);
        Dispose();
    }

    public void Close() => Dispose();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        _outbound.Writer.TryComplete();
        _inbound.Writer.TryComplete();
        _connection.Abort();
        _connection.Dispose();
        lock (_queueLock)
        {
            while (_outbound.Reader.TryRead(out _)) { }
            while (_inbound.Reader.TryRead(out _)) { }
            _outboundBytes = _inboundBytes = 0;
        }
    }
}
