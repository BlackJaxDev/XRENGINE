using System.Net;

namespace XREngine.Networking;

/// <summary>Contains an independently owned received datagram and its remote endpoint.</summary>
public readonly record struct DatagramReceiveResult(byte[] Buffer, IPEndPoint RemoteEndPoint);
