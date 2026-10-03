using System.Buffers.Binary;
using System.Text;

namespace XREngine.Networking;

/// <summary>
/// Binary codec for the clock-synchronization channel. A packet is a fixed 57-byte header followed
/// by the target client id in UTF-8. Writing and reading allocate nothing.
/// </summary>
/// <remarks>
/// Layout (little-endian): session id (16), server player index (4), reserved (4), client send
/// timestamp (8), server receive timestamp (8), server send timestamp (8), server tick (8), client
/// id length (1), client id bytes.
/// </remarks>
public static class ClockSyncPacket
{
    public const int FixedHeaderSize = 57;
    public const int MaxClientIdBytes = 255;
    public const int MaxPacketBytes = FixedHeaderSize + MaxClientIdBytes;

    public static bool TryWrite(Span<byte> destination, in ClockSyncSample sample, ReadOnlySpan<byte> clientIdUtf8, out int bytesWritten)
    {
        bytesWritten = 0;
        if (clientIdUtf8.Length > MaxClientIdBytes)
            return false;

        int total = FixedHeaderSize + clientIdUtf8.Length;
        if (destination.Length < total)
            return false;

        sample.SessionId.TryWriteBytes(destination[..16]);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], sample.ServerPlayerIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], 0u);
        BinaryPrimitives.WriteDoubleLittleEndian(destination[24..], sample.ClientSendTimestampUtc);
        BinaryPrimitives.WriteDoubleLittleEndian(destination[32..], sample.ServerReceiveTimestampUtc);
        BinaryPrimitives.WriteDoubleLittleEndian(destination[40..], sample.ServerSendTimestampUtc);
        BinaryPrimitives.WriteInt64LittleEndian(destination[48..], sample.ServerTickId);
        destination[56] = (byte)clientIdUtf8.Length;
        clientIdUtf8.CopyTo(destination[FixedHeaderSize..]);
        bytesWritten = total;
        return true;
    }

    /// <summary>Encodes a heap message. The client id is transcoded on the stack.</summary>
    public static bool TryWrite(Span<byte> destination, ClockSyncMessage message, out int bytesWritten)
    {
        ArgumentNullException.ThrowIfNull(message);
        bytesWritten = 0;
        string clientId = message.ClientId ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(clientId) > MaxClientIdBytes)
            return false;

        Span<byte> clientIdUtf8 = stackalloc byte[MaxClientIdBytes];
        int clientIdLength = Encoding.UTF8.GetBytes(clientId, clientIdUtf8);
        ClockSyncSample sample = new(
            message.SessionId,
            message.ServerPlayerIndex,
            message.ClientSendTimestampUtc,
            message.ServerReceiveTimestampUtc,
            message.ServerSendTimestampUtc,
            message.ServerTickId);
        return TryWrite(destination, sample, clientIdUtf8[..clientIdLength], out bytesWritten);
    }

    public static bool TryRead(ReadOnlySpan<byte> packet, out ClockSyncSample sample, out ReadOnlySpan<byte> clientIdUtf8)
    {
        sample = default;
        clientIdUtf8 = default;
        if (packet.Length < FixedHeaderSize)
            return false;

        int clientIdLength = packet[56];
        if (packet.Length != FixedHeaderSize + clientIdLength)
            return false;

        sample = new ClockSyncSample(
            new Guid(packet[..16]),
            BinaryPrimitives.ReadInt32LittleEndian(packet[16..]),
            BinaryPrimitives.ReadDoubleLittleEndian(packet[24..]),
            BinaryPrimitives.ReadDoubleLittleEndian(packet[32..]),
            BinaryPrimitives.ReadDoubleLittleEndian(packet[40..]),
            BinaryPrimitives.ReadInt64LittleEndian(packet[48..]));
        clientIdUtf8 = packet.Slice(FixedHeaderSize, clientIdLength);
        return true;
    }

    /// <summary>Materializes a heap message for subscribers of the object-based event. Allocates.</summary>
    public static ClockSyncMessage ToMessage(in ClockSyncSample sample, ReadOnlySpan<byte> clientIdUtf8)
        => new()
        {
            SessionId = sample.SessionId,
            ClientId = Encoding.UTF8.GetString(clientIdUtf8),
            ServerPlayerIndex = sample.ServerPlayerIndex,
            ClientSendTimestampUtc = sample.ClientSendTimestampUtc,
            ServerReceiveTimestampUtc = sample.ServerReceiveTimestampUtc,
            ServerSendTimestampUtc = sample.ServerSendTimestampUtc,
            ServerTickId = sample.ServerTickId,
        };

    /// <summary>Compares UTF-8 client id bytes to a string without allocating.</summary>
    public static bool ClientIdEquals(ReadOnlySpan<byte> clientIdUtf8, string? value, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(value))
            return clientIdUtf8.IsEmpty;

        Span<char> chars = stackalloc char[MaxClientIdBytes];
        try
        {
            if (Encoding.UTF8.GetCharCount(clientIdUtf8) > chars.Length)
                return false;
            int charCount = Encoding.UTF8.GetChars(clientIdUtf8, chars);
            return MemoryExtensions.Equals(chars[..charCount], value.AsSpan(), comparison);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
