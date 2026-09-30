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
}
