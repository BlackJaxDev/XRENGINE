namespace XREngine.Networking;

/// <summary>
/// Zero-copy view over a received replication-delta packet. The spans point into the receive
/// buffer and are valid only for the duration of the handler call.
/// </summary>
public readonly ref struct NetworkDeltaPacketView
{
    internal NetworkDeltaPacketView(scoped in NetworkDeltaPacketHeader header, ReadOnlySpan<byte> entityIdBytes, ReadOnlySpan<byte> payload)
    {
        Header = header;
        EntityIdBytes = entityIdBytes;
        Payload = payload;
    }

    public NetworkDeltaPacketHeader Header { get; }

    /// <summary>Sixteen bytes per network entity id.</summary>
    public ReadOnlySpan<byte> EntityIdBytes { get; }

    /// <summary>Channel-specific delta bytes.</summary>
    public ReadOnlySpan<byte> Payload { get; }

    public int EntityIdCount => EntityIdBytes.Length / NetworkDeltaPacket.EntityIdSize;

    public NetworkEntityId GetEntityId(int index)
        => NetworkEntityId.FromGuid(new Guid(EntityIdBytes.Slice(index * NetworkDeltaPacket.EntityIdSize, NetworkDeltaPacket.EntityIdSize)));

    /// <summary>Materializes a heap envelope for subscribers of the object-based event. Allocates.</summary>
    public NetworkDeltaEnvelope ToEnvelope()
    {
        NetworkEntityId[] entityIds = new NetworkEntityId[EntityIdCount];
        for (int i = 0; i < entityIds.Length; i++)
            entityIds[i] = GetEntityId(i);

        return new NetworkDeltaEnvelope
        {
            SessionId = Header.SessionId,
            Channel = Header.Channel,
            ServerTickId = Header.ServerTickId,
            BaselineTickId = Header.BaselineTickId,
            DeltaSequence = Header.DeltaSequence,
            ServerTimestampUtc = Header.ServerTimestampUtc,
            EntityIds = entityIds,
            Payload = Payload.ToArray(),
        };
    }
}
