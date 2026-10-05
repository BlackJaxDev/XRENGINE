using System.Buffers.Binary;

namespace XREngine.Networking;

/// <summary>Handler for a received replication-delta packet. The view is valid only during the call.</summary>
public delegate void NetworkDeltaPacketHandler(in NetworkDeltaPacketView packet);

/// <summary>
/// Binary codec for the replication-delta channel envelope. A packet is a fixed 52-byte header,
/// the network entity ids, and the channel-specific delta payload. Writing and reading allocate
/// nothing.
/// </summary>
/// <remarks>
/// Layout (little-endian): session id (16), server tick (8), baseline tick (8), server timestamp
/// (8), delta sequence (4), channel (1), reserved (1), entity id count (2), payload length (4).
/// </remarks>
public static class NetworkDeltaPacket
{
    public const int FixedHeaderSize = 52;
    public const int EntityIdSize = 16;
    public const int MaxEntityIds = 4096;

    public static int GetPacketSize(int entityIdCount, int payloadLength)
        => FixedHeaderSize + entityIdCount * EntityIdSize + payloadLength;

    public static bool TryWrite(
        Span<byte> destination,
        in NetworkDeltaPacketHeader header,
        ReadOnlySpan<NetworkEntityId> entityIds,
        ReadOnlySpan<byte> payload,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (entityIds.Length > MaxEntityIds)
            return false;

        int total = GetPacketSize(entityIds.Length, payload.Length);
        if (destination.Length < total)
            return false;

        header.SessionId.TryWriteBytes(destination[..16]);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], header.ServerTickId);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..], header.BaselineTickId);
        BinaryPrimitives.WriteDoubleLittleEndian(destination[32..], header.ServerTimestampUtc);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[40..], header.DeltaSequence);
        destination[44] = (byte)header.Channel;
        destination[45] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[46..], (ushort)entityIds.Length);
        BinaryPrimitives.WriteInt32LittleEndian(destination[48..], payload.Length);

        int offset = FixedHeaderSize;
        for (int i = 0; i < entityIds.Length; i++)
        {
            entityIds[i].Value.TryWriteBytes(destination.Slice(offset, EntityIdSize));
            offset += EntityIdSize;
        }

        payload.CopyTo(destination[offset..]);
        bytesWritten = total;
        return true;
    }

    /// <summary>Encodes a heap envelope.</summary>
    public static bool TryWrite(Span<byte> destination, NetworkDeltaEnvelope envelope, out int bytesWritten)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        NetworkDeltaPacketHeader header = new()
        {
            SessionId = envelope.SessionId,
            ServerTickId = envelope.ServerTickId,
            BaselineTickId = envelope.BaselineTickId,
            ServerTimestampUtc = envelope.ServerTimestampUtc,
            DeltaSequence = envelope.DeltaSequence,
            Channel = envelope.Channel,
        };
        return TryWrite(destination, header, envelope.EntityIds, envelope.Payload, out bytesWritten);
    }

    public static bool TryRead(ReadOnlySpan<byte> packet, out NetworkDeltaPacketView view)
    {
        view = default;
        if (packet.Length < FixedHeaderSize)
            return false;

        int entityIdCount = BinaryPrimitives.ReadUInt16LittleEndian(packet[46..]);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(packet[48..]);
        if (entityIdCount > MaxEntityIds || payloadLength < 0)
            return false;
        if ((long)packet.Length != (long)FixedHeaderSize + (long)entityIdCount * EntityIdSize + payloadLength)
            return false;

        NetworkDeltaPacketHeader header = new()
        {
            SessionId = new Guid(packet[..16]),
            ServerTickId = BinaryPrimitives.ReadInt64LittleEndian(packet[16..]),
            BaselineTickId = BinaryPrimitives.ReadInt64LittleEndian(packet[24..]),
            ServerTimestampUtc = BinaryPrimitives.ReadDoubleLittleEndian(packet[32..]),
            DeltaSequence = BinaryPrimitives.ReadUInt32LittleEndian(packet[40..]),
            Channel = (NetworkReplicationChannel)packet[44],
        };

        ReadOnlySpan<byte> entityIds = packet.Slice(FixedHeaderSize, entityIdCount * EntityIdSize);
        ReadOnlySpan<byte> payload = packet.Slice(FixedHeaderSize + entityIds.Length, payloadLength);
        view = new NetworkDeltaPacketView(header, entityIds, payload);
        return true;
    }
}
