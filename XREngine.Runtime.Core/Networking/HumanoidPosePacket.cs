using System.Buffers.Binary;
using System.Text;

namespace XREngine.Networking;

/// <summary>Handler for a received humanoid pose packet. The view is valid only during the call.</summary>
public delegate void HumanoidPosePacketHandler(in HumanoidPosePacketView packet);

/// <summary>
/// Binary codec for the high-rate humanoid pose channel. A packet is a fixed 56-byte header,
/// the source client id in UTF-8, the network entity ids, and the quantized avatar payload written
/// by <see cref="HumanoidPoseSpanPacketWriter"/>. Writing and reading allocate nothing.
/// </summary>
/// <remarks>
/// Fixed header layout (little-endian): session id (16), server tick (8), baseline tick (8),
/// server timestamp (8), frame sequence (4), baseline sequence (2), kind (1), authority mode (1),
/// channel (1), client id length (1), avatar count (2), entity id count (2), payload length (2).
/// </remarks>
public static class HumanoidPosePacket
{
    public const int FixedHeaderSize = 56;
    public const int EntityIdSize = 16;
    public const int MaxClientIdBytes = 255;
    public const int MaxEntityIds = 16;
    public const int MaxPayloadBytes = 1024;

    /// <summary>Largest packet this codec accepts. Sizes stack and slab buffers.</summary>
    public const int MaxPacketBytes = FixedHeaderSize + MaxClientIdBytes + MaxEntityIds * EntityIdSize + MaxPayloadBytes;

    private const int SessionIdOffset = 0;
    private const int ServerTickOffset = 16;
    private const int BaselineTickOffset = 24;
    private const int ServerTimestampOffset = 32;
    private const int FrameSequenceOffset = 40;
    private const int BaselineSequenceOffset = 44;
    private const int KindOffset = 46;
    private const int AuthorityModeOffset = 47;
    private const int ChannelOffset = 48;
    private const int ClientIdLengthOffset = 49;
    private const int AvatarCountOffset = 50;
    private const int EntityIdCountOffset = 52;
    private const int PayloadLengthOffset = 54;

    /// <summary>Encoded size of a packet with the given variable parts.</summary>
    public static int GetPacketSize(int clientIdUtf8Length, int entityIdCount, int payloadLength)
        => FixedHeaderSize + clientIdUtf8Length + entityIdCount * EntityIdSize + payloadLength;

    /// <summary>Writes a packet into <paramref name="destination"/>. Returns false when it does not fit or a part exceeds its bound.</summary>
    public static bool TryWrite(
        Span<byte> destination,
        in HumanoidPosePacketHeader header,
        ReadOnlySpan<byte> sourceClientIdUtf8,
        ReadOnlySpan<NetworkEntityId> entityIds,
        ReadOnlySpan<byte> payload,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (sourceClientIdUtf8.Length > MaxClientIdBytes || entityIds.Length > MaxEntityIds || payload.Length > MaxPayloadBytes)
            return false;

        int total = GetPacketSize(sourceClientIdUtf8.Length, entityIds.Length, payload.Length);
        if (destination.Length < total)
            return false;

        WriteFixedHeader(destination, header, sourceClientIdUtf8.Length, entityIds.Length, payload.Length);
        int offset = FixedHeaderSize;
        sourceClientIdUtf8.CopyTo(destination[offset..]);
        offset += sourceClientIdUtf8.Length;
        for (int i = 0; i < entityIds.Length; i++)
        {
            entityIds[i].Value.TryWriteBytes(destination.Slice(offset, EntityIdSize));
            offset += EntityIdSize;
        }

        payload.CopyTo(destination[offset..]);
        bytesWritten = total;
        return true;
    }

    /// <summary>Encodes a heap pose frame. Used when session-synchronization messages replay embedded frames.</summary>
    public static bool TryWrite(Span<byte> destination, HumanoidPoseFrame frame, out int bytesWritten)
    {
        ArgumentNullException.ThrowIfNull(frame);
        bytesWritten = 0;

        string clientId = frame.SourceClientId ?? string.Empty;
        Span<byte> clientIdUtf8 = stackalloc byte[MaxClientIdBytes];
        if (Encoding.UTF8.GetByteCount(clientId) > MaxClientIdBytes)
            return false;
        int clientIdLength = Encoding.UTF8.GetBytes(clientId, clientIdUtf8);

        HumanoidPosePacketHeader header = new()
        {
            SessionId = frame.SessionId,
            ServerTickId = frame.ServerTickId,
            BaselineTickId = frame.BaselineTickId,
            ServerTimestampUtc = frame.ServerTimestampUtc,
            FrameSequence = frame.FrameSequence,
            BaselineSequence = frame.BaselineSequence,
            Kind = frame.Kind,
            AuthorityMode = frame.AuthorityMode,
            Channel = frame.Channel,
            AvatarCount = (ushort)Math.Clamp(frame.AvatarCount, 0, ushort.MaxValue),
        };

        return TryWrite(destination, header, clientIdUtf8[..clientIdLength], frame.EntityIds, frame.Payload, out bytesWritten);
    }

    /// <summary>Parses a packet into a view. Rejects truncated packets, trailing bytes, and out-of-range counts.</summary>
    public static bool TryRead(ReadOnlySpan<byte> packet, out HumanoidPosePacketView view)
    {
        view = default;
        if (packet.Length < FixedHeaderSize)
            return false;

        int clientIdLength = packet[ClientIdLengthOffset];
        int entityIdCount = BinaryPrimitives.ReadUInt16LittleEndian(packet[EntityIdCountOffset..]);
        int payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(packet[PayloadLengthOffset..]);
        if (entityIdCount > MaxEntityIds || payloadLength > MaxPayloadBytes)
            return false;
        if (packet.Length != GetPacketSize(clientIdLength, entityIdCount, payloadLength))
            return false;

        byte kind = packet[KindOffset];
        if (kind > (byte)HumanoidPosePacketKind.Delta)
            return false;

        HumanoidPosePacketHeader header = new()
        {
            SessionId = new Guid(packet.Slice(SessionIdOffset, 16)),
            ServerTickId = BinaryPrimitives.ReadInt64LittleEndian(packet[ServerTickOffset..]),
            BaselineTickId = BinaryPrimitives.ReadInt64LittleEndian(packet[BaselineTickOffset..]),
            ServerTimestampUtc = BinaryPrimitives.ReadDoubleLittleEndian(packet[ServerTimestampOffset..]),
            FrameSequence = BinaryPrimitives.ReadUInt32LittleEndian(packet[FrameSequenceOffset..]),
            BaselineSequence = BinaryPrimitives.ReadUInt16LittleEndian(packet[BaselineSequenceOffset..]),
            Kind = (HumanoidPosePacketKind)kind,
            AuthorityMode = (NetworkAuthorityMode)packet[AuthorityModeOffset],
            Channel = (NetworkReplicationChannel)packet[ChannelOffset],
            AvatarCount = BinaryPrimitives.ReadUInt16LittleEndian(packet[AvatarCountOffset..]),
        };

        int offset = FixedHeaderSize;
        ReadOnlySpan<byte> clientId = packet.Slice(offset, clientIdLength);
        offset += clientIdLength;
        ReadOnlySpan<byte> entityIds = packet.Slice(offset, entityIdCount * EntityIdSize);
        offset += entityIds.Length;
        ReadOnlySpan<byte> payload = packet.Slice(offset, payloadLength);

        view = new HumanoidPosePacketView(header, packet, clientId, entityIds, payload);
        return true;
    }

    /// <summary>Rewrites the server-owned fields in place on an already validated packet.</summary>
    public static void StampServerFields(Span<byte> packet, Guid sessionId, long serverTickId, double serverTimestampUtc, NetworkAuthorityMode authorityMode)
    {
        if (packet.Length < FixedHeaderSize)
            throw new ArgumentException("Packet is smaller than the pose header.", nameof(packet));

        sessionId.TryWriteBytes(packet.Slice(SessionIdOffset, 16));
        BinaryPrimitives.WriteInt64LittleEndian(packet[ServerTickOffset..], serverTickId);
        BinaryPrimitives.WriteDoubleLittleEndian(packet[ServerTimestampOffset..], serverTimestampUtc);
        packet[AuthorityModeOffset] = (byte)authorityMode;
    }

    private static void WriteFixedHeader(Span<byte> destination, in HumanoidPosePacketHeader header, int clientIdLength, int entityIdCount, int payloadLength)
    {
        header.SessionId.TryWriteBytes(destination.Slice(SessionIdOffset, 16));
        BinaryPrimitives.WriteInt64LittleEndian(destination[ServerTickOffset..], header.ServerTickId);
        BinaryPrimitives.WriteInt64LittleEndian(destination[BaselineTickOffset..], header.BaselineTickId);
        BinaryPrimitives.WriteDoubleLittleEndian(destination[ServerTimestampOffset..], header.ServerTimestampUtc);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[FrameSequenceOffset..], header.FrameSequence);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[BaselineSequenceOffset..], header.BaselineSequence);
        destination[KindOffset] = (byte)header.Kind;
        destination[AuthorityModeOffset] = (byte)header.AuthorityMode;
        destination[ChannelOffset] = (byte)header.Channel;
        destination[ClientIdLengthOffset] = (byte)clientIdLength;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[AvatarCountOffset..], header.AvatarCount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[EntityIdCountOffset..], (ushort)entityIdCount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[PayloadLengthOffset..], (ushort)payloadLength);
    }
}
