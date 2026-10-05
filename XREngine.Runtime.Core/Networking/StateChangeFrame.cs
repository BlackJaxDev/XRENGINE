using System.Buffers.Binary;

namespace XREngine.Networking;

/// <summary>
/// Fixed sub-header for a realtime state change, written directly into the packet buffer ahead of
/// the payload bytes.
/// </summary>
/// <remarks>
/// Layout (little-endian): byte 0 is the <see cref="EStateChangeType"/>, byte 1 is the low byte of
/// <see cref="RealtimeProtocol.WireVersion"/>, bytes 2..3 are reserved and zero, and bytes 4..7 are
/// the payload length. The payload follows immediately.
/// </remarks>
public static class StateChangeFrame
{
    public const int HeaderSize = 8;

    /// <summary>Upper bound on a state-change payload. Enforced on the header before any decoding.</summary>
    public const int MaxPayloadBytes = 262_144;

    private const byte HighestDefinedType = (byte)EStateChangeType.ReplicationSyncComplete;

    /// <summary>Writes the sub-header into the first <see cref="HeaderSize"/> bytes of <paramref name="destination"/>.</summary>
    public static void WriteHeader(Span<byte> destination, EStateChangeType type, int payloadLength)
    {
        if (destination.Length < HeaderSize)
            throw new ArgumentException("Destination is smaller than the state-change header.", nameof(destination));
        if ((uint)payloadLength > MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payloadLength), $"State-change payload exceeds the {MaxPayloadBytes}-byte limit.");

        destination[0] = (byte)type;
        destination[1] = unchecked((byte)RealtimeProtocol.WireVersion);
        destination[2] = 0;
        destination[3] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], payloadLength);
    }

    /// <summary>
    /// Validates the sub-header and slices the payload. Rejects truncated, oversized, unknown-type,
    /// mismatched-protocol, and over-long frames without touching the payload bytes.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> frame, out EStateChangeType type, out ReadOnlySpan<byte> payload, out EStateChangeFrameError error)
    {
        type = EStateChangeType.Invalid;
        payload = default;
        if (frame.Length < HeaderSize)
        {
            error = EStateChangeFrameError.Truncated;
            return false;
        }

        byte rawType = frame[0];
        if (frame[1] != unchecked((byte)RealtimeProtocol.WireVersion))
        {
            error = EStateChangeFrameError.ProtocolMismatch;
            return false;
        }

        if (rawType == (byte)EStateChangeType.Invalid || rawType > HighestDefinedType)
        {
            error = EStateChangeFrameError.UnknownType;
            return false;
        }

        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(frame[4..]);
        if (payloadLength < 0 || payloadLength > MaxPayloadBytes)
        {
            error = EStateChangeFrameError.Oversized;
            return false;
        }

        int available = frame.Length - HeaderSize;
        if (payloadLength > available)
        {
            error = EStateChangeFrameError.Truncated;
            return false;
        }

        if (payloadLength != available)
        {
            error = EStateChangeFrameError.LengthMismatch;
            return false;
        }

        type = (EStateChangeType)rawType;
        payload = frame.Slice(HeaderSize, payloadLength);
        error = EStateChangeFrameError.None;
        return true;
    }

    /// <summary>Reads the wire version byte a peer wrote, for mismatch diagnostics.</summary>
    public static int PeekWireVersion(ReadOnlySpan<byte> frame)
        => frame.Length > 1 ? frame[1] : -1;
}
