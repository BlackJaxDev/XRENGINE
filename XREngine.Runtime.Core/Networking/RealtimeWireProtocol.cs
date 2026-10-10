namespace XREngine.Networking;

/// <summary>Coordinates a strict wire revision across datagram framing and encrypted transports.</summary>
public static class RealtimeWireProtocol
{
    public const byte Version = 2;
    public const string TlsProtocol = "xrengine-realtime/2";
    public const string WebSocketProtocol = "xrengine-realtime.v2";
    public const string UpdateRequiredMessage = "Realtime wire version 2 is required; update client and server together.";
    public static ReadOnlySpan<byte> FrameMagic => "FR2"u8;

    /// <summary>Recognizes incompatible realtime headers before they can mutate peer, admission, or ACK state.</summary>
    public static bool IsIncompatible(ReadOnlySpan<byte> datagram)
        => (datagram.Length >= 5 && datagram[..4].SequenceEqual("XRMU"u8) && datagram[4] != Version)
            || (datagram.Length >= 3 && datagram[..2].SequenceEqual("FR"u8) && datagram[2] != FrameMagic[2]);
}
