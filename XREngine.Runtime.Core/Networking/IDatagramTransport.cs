using System.Net;

namespace XREngine.Networking;

/// <summary>Owns datagram I/O independently of realtime framing, admission, and replication.</summary>
public interface IDatagramTransport : IDisposable
{
    int Available { get; }
    bool IsBound { get; }
    bool Connected { get; }
    IPEndPoint? LocalEndPoint { get; }
    bool ExclusiveAddressUse { get; set; }
    bool MulticastLoopback { get; set; }
    bool EnableBroadcast { get; set; }
    bool ReuseAddress { set; }
    void Bind(IPEndPoint endpoint);
    void Connect(IPEndPoint endpoint);
    void JoinMulticastGroup(IPAddress address);
    void Close();
    int Send(byte[] buffer, int count, IPEndPoint endpoint);
    Task<int> SendAsync(byte[] buffer, int count, IPEndPoint endpoint);
    ValueTask<DatagramReceiveResult> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True when <see cref="TryReceive"/> and the span <see cref="Send(ReadOnlySpan{byte}, IPEndPoint)"/>
    /// overload run without allocating. The realtime receive loop uses the span path only when this is set.
    /// </summary>
    bool SupportsSpanDatagrams => false;

    /// <summary>
    /// Sends one datagram from a span. The default copies into an array for transports that only
    /// implement the array overload. Socket transports override it to send without allocating.
    /// </summary>
    int Send(ReadOnlySpan<byte> buffer, IPEndPoint endpoint)
        => Send(buffer.ToArray(), buffer.Length, endpoint);

    /// <summary>
    /// Receives one pending datagram into caller-owned storage without blocking. Returns false when
    /// nothing is pending. A datagram larger than <paramref name="destination"/> is discarded and
    /// reported with <paramref name="bytesReceived"/> set to -1 so the caller can count it.
    /// The returned endpoint instance is cached per remote address and must not be mutated.
    /// </summary>
    bool TryReceive(Span<byte> destination, out int bytesReceived, out IPEndPoint? remoteEndPoint)
    {
        bytesReceived = 0;
        remoteEndPoint = null;
        return false;
    }
}
