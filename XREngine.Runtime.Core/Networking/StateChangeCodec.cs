using System.Buffers;
using MemoryPack;

namespace XREngine.Networking;

/// <summary>
/// Binary codecs for state-change payloads. Control messages use their MemoryPack-generated
/// formatters, writing to an <see cref="IBufferWriter{T}"/> and reading from a span. The high-rate
/// pose, clock-sync, and replication-delta channels use the dedicated packet codecs instead
/// (<see cref="HumanoidPosePacket"/>, <see cref="ClockSyncPacket"/>, <see cref="NetworkDeltaPacket"/>).
/// </summary>
public static class StateChangeCodec
{
    [ThreadStatic]
    private static RealtimeScratchBufferWriter? _measureWriterA;

    [ThreadStatic]
    private static RealtimeScratchBufferWriter? _measureWriterB;

    /// <summary>Writes <paramref name="payload"/> to <paramref name="writer"/> with its generated formatter.</summary>
    public static void Write<T>(IBufferWriter<byte> writer, in T payload)
    {
        ArgumentNullException.ThrowIfNull(writer);
        MemoryPackSerializer.Serialize(writer, payload);
    }

    /// <summary>
    /// Reads a payload from a span. Decoder failures return false; they never escape to the receive
    /// loop. The caller has already bounded the span through <see cref="StateChangeFrame.TryRead"/>.
    /// </summary>
    public static bool TryRead<T>(ReadOnlySpan<byte> payload, out T? value)
    {
        value = default;
        if (payload.IsEmpty)
            return false;

        try
        {
            value = MemoryPackSerializer.Deserialize<T>(payload);
            return value is not null;
        }
        catch (Exception ex) when (ex is MemoryPackSerializationException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or OverflowException or NotSupportedException)
        {
            value = default;
            return false;
        }
    }

    /// <summary>Encoded size of a payload, measured with a reusable per-thread buffer.</summary>
    public static int GetEncodedLength<T>(in T payload)
    {
        RealtimeScratchBufferWriter writer = _measureWriterA ??= new RealtimeScratchBufferWriter(4096);
        writer.Reset();
        MemoryPackSerializer.Serialize(writer, payload);
        return writer.WrittenCount;
    }

    /// <summary>True when two payloads encode to identical bytes. Used to detect conflicting duplicates.</summary>
    public static bool EncodedEquals<T>(in T left, in T right)
    {
        RealtimeScratchBufferWriter a = _measureWriterA ??= new RealtimeScratchBufferWriter(4096);
        RealtimeScratchBufferWriter b = _measureWriterB ??= new RealtimeScratchBufferWriter(4096);
        a.Reset();
        b.Reset();
        MemoryPackSerializer.Serialize(a, left);
        MemoryPackSerializer.Serialize(b, right);
        return a.WrittenSpan.SequenceEqual(b.WrittenSpan);
    }

    /// <summary>
    /// The DTO a control state-change type carries, or null for types that use a dedicated packet
    /// codec or an opaque host-defined blob.
    /// </summary>
    public static Type? GetPayloadType(EStateChangeType type)
        => type switch
        {
            EStateChangeType.PlayerJoin => typeof(PlayerJoinRequest),
            EStateChangeType.PlayerAssignment => typeof(PlayerAssignment),
            EStateChangeType.PlayerLeave => typeof(PlayerLeaveNotice),
            EStateChangeType.Heartbeat => typeof(PlayerHeartbeat),
            EStateChangeType.PlayerInputSnapshot => typeof(PlayerInputSnapshot),
            EStateChangeType.PlayerTransformUpdate => typeof(PlayerTransformUpdate),
            EStateChangeType.RemoteJobRequest => typeof(RemoteJobRequest),
            EStateChangeType.RemoteJobResponse => typeof(RemoteJobResponse),
            EStateChangeType.ServerError => typeof(ServerErrorMessage),
            EStateChangeType.AuthorityLeaseUpdate => typeof(NetworkAuthorityLease),
            EStateChangeType.ReplicationSnapshot => typeof(NetworkSnapshotEnvelope),
            EStateChangeType.ReplicationBaselineChunk => typeof(ReplicationBaselineChunk),
            EStateChangeType.ReplicationDeltaBatch => typeof(ReplicationDeltaBatch),
            EStateChangeType.ReplicationTransferAck => typeof(ReplicationTransferAck),
            EStateChangeType.ReplicationResyncRequest => typeof(ReplicationResyncRequest),
            EStateChangeType.ReplicationSyncComplete => typeof(ReplicationSyncComplete),
            _ => null,
        };
}
