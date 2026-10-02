using System.Buffers.Binary;
using System.Net;
using XREngine.Networking;

namespace XREngine;

/// <summary>
/// The unreliable high-rate lane. Pose, clock-sync, and replication-delta packets are composed on
/// the stack, copied into a preallocated per-peer ring, and transmitted from a reusable buffer, so
/// a steady-state send performs no heap allocation. A full ring drops its oldest packet.
/// </summary>
public abstract partial class BaseNetworkingManager
{
    /// <summary>Largest inner frame the high-rate lane carries. Larger payloads use the reliable-lane framing.</summary>
    public const int HighRateSlotBytes = 1200;
    /// <summary>Packets retained per peer between send pumps before the oldest is dropped.</summary>
    public const int HighRateRingCapacity = 64;

    private const int HighRateRttWindow = 64;
    private const int HighRateRttMask = HighRateRttWindow - 1;

    /// <summary>Largest state-change payload that fits one high-rate slot.</summary>
    public const int MaxHighRatePayloadBytes = HighRateSlotBytes - HeaderLen - GuidLen - StateChangeFrame.HeaderSize;

    /// <summary>Holds one dequeued high-rate frame while it is stamped and protected. Only the send pump touches it.</summary>
    private readonly byte[] _highRateDrainScratch = new byte[HighRateSlotBytes];

    /// <summary>Sends a state change to every current send target on the high-rate lane.</summary>
    protected void SendHighRateStateChange(EStateChangeType type, ReadOnlySpan<byte> payload)
    {
        using var measurement = HighRateSendMeasurements.Begin(payload.Length);
        if (payload.Length > MaxHighRatePayloadBytes)
        {
            Interlocked.Increment(ref _highRateOversizeFallbacks);
            ReplicateStateChange(type, payload, compress: false, resendOnFailedAck: false);
            return;
        }

        Span<byte> frame = stackalloc byte[HighRateSlotBytes];
        int frameLength = ComposeHighRateFrame(frame, type, payload);
        lock (_sendTargetSync)
        {
            _sendTargetsScratch.Clear();
            CollectUdpSendTargets(_sendTargetsScratch);
            for (int i = 0; i < _sendTargetsScratch.Count; i++)
                EnqueueHighRate(RegisterUdpPeer(_sendTargetsScratch[i]), frame[..frameLength]);
            _sendTargetsScratch.Clear();
        }
    }

    /// <summary>Sends a state change to one target on the high-rate lane.</summary>
    protected void SendHighRateStateChangeTo(IPEndPoint target, EStateChangeType type, ReadOnlySpan<byte> payload)
    {
        using var measurement = HighRateSendMeasurements.Begin(payload.Length);
        ArgumentNullException.ThrowIfNull(target);
        if (payload.Length > MaxHighRatePayloadBytes)
        {
            Interlocked.Increment(ref _highRateOversizeFallbacks);
            RealtimeScratchBufferWriter writer = BeginStateChangeFrame();
            payload.CopyTo(writer.GetSpan(payload.Length));
            writer.Advance(payload.Length);
            SendToTarget(target, Guid.Empty, false, CompleteStateChangeFrame(writer, type), EBroadcastType.StateChange, false);
            return;
        }

        Span<byte> frame = stackalloc byte[HighRateSlotBytes];
        int frameLength = ComposeHighRateFrame(frame, type, payload);
        EnqueueHighRate(RegisterUdpPeer(target), frame[..frameLength]);
    }

    /// <summary>
    /// Broadcasts a humanoid pose on the high-rate lane. The role fills the session, identity, and
    /// sequencing fields; <paramref name="avatarPayload"/> may be rewritten in place to carry the
    /// server-assigned avatar id.
    /// </summary>
    /// <returns>False when the role is not ready to send gameplay traffic or the pose is not sendable.</returns>
    public bool BroadcastHumanoidPose(HumanoidPosePacketKind kind, ushort baselineSequence, int avatarCount, Span<byte> avatarPayload)
    {
        if (avatarCount <= 0 || avatarCount > ushort.MaxValue || avatarPayload.Length > HumanoidPosePacket.MaxPayloadBytes)
            return false;

        HumanoidPosePacketHeader header = new()
        {
            Kind = kind,
            BaselineSequence = baselineSequence,
            AvatarCount = (ushort)avatarCount,
            Channel = NetworkReplicationChannel.HumanoidPose,
        };

        Span<byte> clientIdUtf8 = stackalloc byte[HumanoidPosePacket.MaxClientIdBytes];
        Span<NetworkEntityId> entityIds = stackalloc NetworkEntityId[HumanoidPosePacket.MaxEntityIds];
        if (!TryPrepareOutgoingHumanoidPose(ref header, avatarPayload, clientIdUtf8, out int clientIdLength, entityIds, out int entityIdCount))
            return false;

        Span<byte> packet = stackalloc byte[HumanoidPosePacket.MaxPacketBytes];
        if (!HumanoidPosePacket.TryWrite(packet, header, clientIdUtf8[..clientIdLength], entityIds[..entityIdCount], avatarPayload, out int written))
            return false;

        SendHighRateStateChange(EStateChangeType.HumanoidPoseFrame, packet[..written]);
        return true;
    }

    /// <summary>
    /// Fills the role-owned fields of an outgoing pose. Return false to suppress the send. The
    /// default sends the pose with an empty identity.
    /// </summary>
    protected virtual bool TryPrepareOutgoingHumanoidPose(
        ref HumanoidPosePacketHeader header,
        Span<byte> avatarPayload,
        Span<byte> clientIdUtf8,
        out int clientIdLength,
        Span<NetworkEntityId> entityIds,
        out int entityIdCount)
    {
        clientIdLength = 0;
        entityIdCount = 0;
        return true;
    }

    /// <summary>Sends a clock-sync reply to one peer on the high-rate lane.</summary>
    protected void SendClockSyncTo(IPEndPoint target, in ClockSyncSample sample, string? clientId)
    {
        Span<byte> clientIdUtf8 = stackalloc byte[ClockSyncPacket.MaxClientIdBytes];
        if (!TryEncodeUtf8(clientId, clientIdUtf8, out int clientIdLength))
            return;

        Span<byte> packet = stackalloc byte[ClockSyncPacket.MaxPacketBytes];
        if (ClockSyncPacket.TryWrite(packet, sample, clientIdUtf8[..clientIdLength], out int written))
            SendHighRateStateChangeTo(target, EStateChangeType.ClockSync, packet[..written]);
    }

    /// <summary>Broadcasts a clock-sync message on the high-rate lane.</summary>
    protected void BroadcastClockSync(in ClockSyncSample sample, string? clientId)
    {
        Span<byte> clientIdUtf8 = stackalloc byte[ClockSyncPacket.MaxClientIdBytes];
        if (!TryEncodeUtf8(clientId, clientIdUtf8, out int clientIdLength))
            return;

        Span<byte> packet = stackalloc byte[ClockSyncPacket.MaxPacketBytes];
        if (ClockSyncPacket.TryWrite(packet, sample, clientIdUtf8[..clientIdLength], out int written))
            SendHighRateStateChange(EStateChangeType.ClockSync, packet[..written]);
    }

    /// <summary>
    /// Broadcasts a replication delta from caller-owned spans. A delta that fits one high-rate
    /// slot is sent without allocating; a larger one is counted and sent with reliable-lane framing.
    /// </summary>
    public void BroadcastReplicationDelta(in NetworkDeltaPacketHeader header, ReadOnlySpan<NetworkEntityId> entityIds, ReadOnlySpan<byte> payload)
    {
        int packetSize = NetworkDeltaPacket.GetPacketSize(entityIds.Length, payload.Length);
        if (packetSize <= MaxHighRatePayloadBytes)
        {
            Span<byte> packet = stackalloc byte[MaxHighRatePayloadBytes];
            if (NetworkDeltaPacket.TryWrite(packet, header, entityIds, payload, out int written))
                SendHighRateStateChange(EStateChangeType.ReplicationDelta, packet[..written]);
            return;
        }

        SendOversizedReplicationDelta(header, entityIds, payload, packetSize, compress: false, resendOnFailedAck: false, DefaultAckTimeoutSec);
    }

    /// <summary>
    /// Broadcasts a replication delta envelope. It uses the high-rate lane unless the caller asks
    /// for acknowledged delivery or compression, or the delta exceeds one high-rate slot.
    /// </summary>
    public void BroadcastReplicationDelta(NetworkDeltaEnvelope envelope, bool compress = false, bool resendOnFailedAck = false, float maxAckWaitSec = DefaultAckTimeoutSec)
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

        int packetSize = NetworkDeltaPacket.GetPacketSize(envelope.EntityIds.Length, envelope.Payload.Length);
        if (!compress && !resendOnFailedAck && packetSize <= MaxHighRatePayloadBytes)
        {
            BroadcastReplicationDelta(header, envelope.EntityIds, envelope.Payload);
            return;
        }

        SendOversizedReplicationDelta(header, envelope.EntityIds, envelope.Payload, packetSize, compress, resendOnFailedAck, maxAckWaitSec);
    }

    private void SendOversizedReplicationDelta(in NetworkDeltaPacketHeader header, ReadOnlySpan<NetworkEntityId> entityIds, ReadOnlySpan<byte> payload, int packetSize, bool compress, bool resendOnFailedAck, float maxAckWaitSec)
    {
        if (packetSize > StateChangeFrame.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), $"Replication delta exceeds the {StateChangeFrame.MaxPayloadBytes}-byte state-change limit.");

        if (packetSize > MaxHighRatePayloadBytes)
            Interlocked.Increment(ref _highRateOversizeFallbacks);

        RealtimeScratchBufferWriter writer = BeginStateChangeFrame();
        if (!NetworkDeltaPacket.TryWrite(writer.GetSpan(packetSize), header, entityIds, payload, out int written))
            throw new InvalidOperationException("Replication delta could not be encoded.");
        writer.Advance(written);
        Send(Guid.Empty, compress, CompleteStateChangeFrame(writer, EStateChangeType.ReplicationDelta), EBroadcastType.StateChange, resendOnFailedAck, maxAckWaitSec);
    }

    /// <summary>
    /// Writes an uncompressed state-change FRK frame with zeroed sequence fields. The sequence and
    /// acknowledgement are stamped when the frame is transmitted.
    /// </summary>
    private static int ComposeHighRateFrame(Span<byte> frame, EStateChangeType type, ReadOnlySpan<byte> payload)
    {
        int dataLength = StateChangeFrame.HeaderSize + payload.Length;
        WriteFrameHeader(frame, EncodeFlags(false, EBroadcastType.StateChange), dataLength);
        frame.Slice(HeaderLen, GuidLen).Clear();
        StateChangeFrame.WriteHeader(frame[(HeaderLen + GuidLen)..], type, payload.Length);
        payload.CopyTo(frame[(HeaderLen + GuidLen + StateChangeFrame.HeaderSize)..]);
        return HeaderLen + GuidLen + dataLength;
    }

    private void EnqueueHighRate(UdpPeerState peer, ReadOnlySpan<byte> frame)
    {
        lock (peer.Sync)
        {
            // One-time allocation per peer. Peers that only ever receive never pay for a ring.
            RealtimePacketSendRing ring = peer.HighRateRing ??= new RealtimePacketSendRing(HighRateRingCapacity, HighRateSlotBytes);
            peer.HighRateSendTicks ??= new long[HighRateRttWindow];
            peer.HighRateSendSequences ??= new ushort[HighRateRttWindow];
            if (ring.Count == ring.Capacity)
                Interlocked.Increment(ref _highRateQueueDrops);
            ring.TryEnqueue(frame, RealtimePacketDropPolicy.DropOldest);
        }
    }

    /// <summary>Transmits queued high-rate frames for one peer and returns how many were sent.</summary>
    private int DrainHighRateRing(IDatagramTransport? client, UdpPeerState peer, int packetsAllowed)
    {
        RealtimePacketSendRing? ring = peer.HighRateRing;
        if (ring is null || packetsAllowed <= 0)
            return 0;

        byte[] inner = _highRateDrainScratch;
        byte[] scratch = _sendScratch;
        int sent = 0;
        while (sent < packetsAllowed)
        {
            int innerLength;
            lock (peer.Sync)
            {
                if (!ring.TryDequeue(out ReadOnlyMemory<byte> packet))
                    break;

                packet.Span.CopyTo(inner);
                innerLength = packet.Length;
                if (client is not null)
                {
                    NextSequence_NoLock(peer, out ushort sequence, out ushort ack, out uint ackBits);
                    WriteSequenceFields(inner, sequence, ack, ackBits);
                    int slot = sequence & HighRateRttMask;
                    peer.HighRateSendSequences![slot] = sequence;
                    // Zero marks an empty slot, so a send at tick zero is recorded as tick one.
                    peer.HighRateSendTicks![slot] = Math.Max(1L, CurrentEngineTicks());
                }
            }

            if (client is null)
                continue;

            if (!TryProtectOutboundDatagram(inner.AsSpan(0, innerLength), peer.EndPoint, scratch, out int written))
            {
                // Unreliable traffic is never retained for a peer whose transport key is not routable.
                Interlocked.Increment(ref _highRateQueueDrops);
                continue;
            }

            client.Send(scratch.AsSpan(0, written), peer.EndPoint);
            RecordBytesSent(written);
            sent++;
        }

        return sent;
    }

    /// <summary>Completes a round-trip sample for a high-rate packet from its fixed send-time window.</summary>
    private bool TryAcknowledgeHighRateSequence(UdpPeerState peer, ushort ackedSeq)
    {
        long sentTicks;
        lock (peer.Sync)
        {
            long[]? ticks = peer.HighRateSendTicks;
            ushort[]? sequences = peer.HighRateSendSequences;
            if (ticks is null || sequences is null)
                return false;

            int slot = ackedSeq & HighRateRttMask;
            sentTicks = ticks[slot];
            if (sentTicks == 0L || sequences[slot] != ackedSeq)
                return false;

            ticks[slot] = 0L;
        }

        UpdateRTT((float)TickDeltaToSeconds(CurrentEngineTicks(), sentTicks));
        return true;
    }
}
