using System.Text;

namespace XREngine.Networking;

/// <summary>
/// Zero-copy view over a received humanoid pose packet. The spans point into the receive slab and
/// are valid only for the duration of the handler call; copy what must be retained.
/// </summary>
public readonly ref struct HumanoidPosePacketView
{
    internal HumanoidPosePacketView(
        scoped in HumanoidPosePacketHeader header,
        ReadOnlySpan<byte> rawPacket,
        ReadOnlySpan<byte> sourceClientIdUtf8,
        ReadOnlySpan<byte> entityIdBytes,
        ReadOnlySpan<byte> payload)
    {
        Header = header;
        RawPacket = rawPacket;
        SourceClientIdUtf8 = sourceClientIdUtf8;
        EntityIdBytes = entityIdBytes;
        Payload = payload;
    }

    public HumanoidPosePacketHeader Header { get; }

    /// <summary>The complete encoded packet, for relay or retention.</summary>
    public ReadOnlySpan<byte> RawPacket { get; }

    /// <summary>UTF-8 bytes of the source client identity.</summary>
    public ReadOnlySpan<byte> SourceClientIdUtf8 { get; }

    /// <summary>Sixteen bytes per network entity id.</summary>
    public ReadOnlySpan<byte> EntityIdBytes { get; }

    /// <summary>Quantized avatar records for <see cref="HumanoidPosePacketCursor"/>.</summary>
    public ReadOnlySpan<byte> Payload { get; }

    public int EntityIdCount => EntityIdBytes.Length / HumanoidPosePacket.EntityIdSize;

    public NetworkEntityId GetEntityId(int index)
        => NetworkEntityId.FromGuid(new Guid(EntityIdBytes.Slice(index * HumanoidPosePacket.EntityIdSize, HumanoidPosePacket.EntityIdSize)));

    /// <summary>Compares the source client id to a string without allocating.</summary>
    public bool SourceClientIdEquals(string? value, StringComparison comparison = StringComparison.Ordinal)
    {
        if (string.IsNullOrEmpty(value))
            return SourceClientIdUtf8.IsEmpty;

        Span<char> chars = stackalloc char[HumanoidPosePacket.MaxClientIdBytes];
        if (!TryDecodeSourceClientId(chars, out int charCount))
            return false;

        return MemoryExtensions.Equals(chars[..charCount], value.AsSpan(), comparison);
    }

    /// <summary>Decodes the source client id into caller-provided characters.</summary>
    public bool TryDecodeSourceClientId(Span<char> destination, out int charCount)
    {
        charCount = 0;
        if (SourceClientIdUtf8.IsEmpty)
            return true;

        try
        {
            if (Encoding.UTF8.GetCharCount(SourceClientIdUtf8) > destination.Length)
                return false;
            charCount = Encoding.UTF8.GetChars(SourceClientIdUtf8, destination);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Materializes the packet as a heap <see cref="HumanoidPoseFrame"/>. This allocates and exists
    /// for the session-synchronization messages that embed pose frames; the per-packet path uses
    /// the view directly.
    /// </summary>
    public HumanoidPoseFrame ToFrame()
    {
        NetworkEntityId[] entityIds = new NetworkEntityId[EntityIdCount];
        for (int i = 0; i < entityIds.Length; i++)
            entityIds[i] = GetEntityId(i);

        return new HumanoidPoseFrame
        {
            SessionId = Header.SessionId,
            SourceClientId = Encoding.UTF8.GetString(SourceClientIdUtf8),
            Channel = Header.Channel,
            Kind = Header.Kind,
            BaselineSequence = Header.BaselineSequence,
            ServerTickId = Header.ServerTickId,
            BaselineTickId = Header.BaselineTickId,
            FrameSequence = Header.FrameSequence,
            ServerTimestampUtc = Header.ServerTimestampUtc,
            AuthorityMode = Header.AuthorityMode,
            EntityIds = entityIds,
            AvatarCount = Header.AvatarCount,
            Payload = Payload.ToArray(),
        };
    }
}
