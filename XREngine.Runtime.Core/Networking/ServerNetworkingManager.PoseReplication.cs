using System.Net;
using XREngine.Networking;

namespace XREngine;

public partial class ServerNetworkingManager
{
    private readonly object _poseReplicationLock = new();
    private readonly Dictionary<int, CanonicalPoseState> _canonicalPoses = [];
    private readonly object _poseQueueLock = new();
    private readonly PersistentReceiveSlabPool _poseSlabs = new(256, HumanoidPosePacket.MaxPacketBytes);
    private readonly Queue<(PersistentReceiveSlab Slab, IPEndPoint Sender, Guid Generation)> _poseQueue = new(256);
    private readonly Action _drainPoseQueue;
    private bool _poseDrainQueued;
    private bool _poseQueueDisposed;

    private void QueuePosePacket(ReadOnlySpan<byte> payload, IPEndPoint sender)
    {
        if (!HumanoidPosePacket.TryRead(payload, out HumanoidPosePacketView packet))
        {
            RecordPayloadDecodeFailure();
            return;
        }
        Guid generation;
        lock (_playerLock)
        {
            if (!TryResolvePoseOwner(packet, sender, out NetworkPlayerConnection connection))
                return;
            generation = connection.ReplicationConnectionGeneration;
        }
        lock (_poseQueueLock)
        {
            if (_poseQueueDisposed)
                return;
            if (!_poseSlabs.TryRent(out PersistentReceiveSlab slab))
            {
                RecordPoseSlabExhausted();
                return;
            }
            payload.CopyTo(slab.WritableSpan);
            slab.Length = payload.Length;
            _poseQueue.Enqueue((slab, sender, generation));
            if (_poseDrainQueued)
                return;
            _poseDrainQueued = true;
        }
        RuntimeNetworkingHostServices.Current.EnqueueSimulation(_drainPoseQueue);
    }

    private void DrainPoseQueue()
    {
        while (true)
        {
            (PersistentReceiveSlab Slab, IPEndPoint Sender, Guid Generation) item;
            lock (_poseQueueLock)
            {
                if (!_poseQueue.TryDequeue(out item))
                {
                    _poseDrainQueued = false;
                    return;
                }
            }
            try
            {
                using var measurement = PoseRelayMeasurements.Begin(item.Slab.Length);
                if (!TryAcceptPosePacket(item.Slab.Span, item.Sender, item.Generation))
                {
                    RecordPayloadDecodeFailure();
                    continue;
                }
                if (HumanoidPosePacket.TryRead(item.Slab.Span, out HumanoidPosePacketView accepted))
                {
                    RaiseHumanoidPosePacketReceived(accepted);
                    // The canonical cache is materialized by session synchronization only.
                    // A pose must not force an allocating reliable snapshot on each send tick.
                    lock (_playerLock)
                        foreach (NetworkPlayerConnection recipient in _playersByIndex.Values)
                            if (recipient.SessionId == accepted.Header.SessionId && recipient.LastEndpoint is { } target)
                                SendHighRateStateChangeTo(target, EStateChangeType.HumanoidPoseFrame, accepted.RawPacket);
                }
            }
            catch (Exception exception)
            {
                Debug.NetworkingWarning("[Server] Pose dispatch failed: {0}", exception.Message);
            }
            finally
            {
                lock (_poseQueueLock)
                    _poseSlabs.Return(item.Slab);
            }
        }
    }

    private bool TryResolvePoseOwner(in HumanoidPosePacketView packet, IPEndPoint sender, out NetworkPlayerConnection connection)
    {
        foreach (NetworkPlayerConnection candidate in _playersByIndex.Values)
            if (packet.SourceClientIdEquals(candidate.ClientId)
                && IsAuthenticatedSender(candidate, sender, packet.Header.SessionId))
            {
                connection = candidate;
                return true;
            }
        connection = null!;
        return false;
    }

    /// <summary>Validates and retains a pose supplied by an explicit object-based caller.</summary>
    public bool TryAcceptHumanoidPoseFrame(HumanoidPoseFrame frame, IPEndPoint sender, out HumanoidPoseFrame accepted)
    {
        accepted = null!;
        Span<byte> bytes = stackalloc byte[HumanoidPosePacket.MaxPacketBytes];
        if (!HumanoidPosePacket.TryWrite(bytes, frame, out int written)
            || !TryAcceptPosePacket(bytes[..written], sender, null)
            || !HumanoidPosePacket.TryRead(bytes[..written], out HumanoidPosePacketView packet))
            return false;
        accepted = packet.ToFrame();
        return true;
    }

    private bool TryAcceptPosePacket(Span<byte> bytes, IPEndPoint sender, Guid? generation)
    {
        if (!HumanoidPosePacket.TryRead(bytes, out HumanoidPosePacketView packet))
            return false;
        lock (_playerLock)
        {
            if (!TryResolvePoseOwner(packet, sender, out NetworkPlayerConnection connection)
                || (generation.HasValue && generation != connection.ReplicationConnectionGeneration))
                return false;
            if (!RequiresManagedUdpTransport)
            {
                // Direct sessions retain their multi-avatar payload contract. Unlike managed
                // admissions, their avatar record identifiers are chosen by the local components.
                if (packet.Header.Channel != NetworkReplicationChannel.HumanoidPose
                    || packet.Header.AvatarCount == 0 || packet.Payload.IsEmpty)
                    return false;
                double nowUtc = GetUtcSeconds();
                if (packet.EntityIdCount == 0)
                {
                    if (!ValidateAuthority(connection, connection.NetworkEntityId, nowUtc, out _))
                        return false;
                }
                else
                    for (int i = 0; i < packet.EntityIdCount; i++)
                        if (!ValidateAuthority(connection, packet.GetEntityId(i), nowUtc, out _))
                            return false;
                long directTick = _replication.CurrentServerTickId == 0 ? _replication.AdvanceServerTick() : _replication.CurrentServerTickId;
                HumanoidPosePacket.StampServerFields(bytes, connection.SessionId, directTick, nowUtc, NetworkAuthorityMode.ServerAuthoritative);
                connection.LastHeardUtc = DateTime.UtcNow;
                return true;
            }
            if (!TryPreflightManagedHumanoidPoseFrame(connection, packet)
                || !ValidateAuthority(connection, connection.NetworkEntityId, GetUtcSeconds(), out _))
                return false;
            lock (_poseReplicationLock)
            {
                _canonicalPoses.TryGetValue(connection.ServerPlayerIndex, out CanonicalPoseState? state);
                if (state is not null && (state.SessionId != connection.SessionId || state.ClientId != connection.ClientId))
                    state = null;
                if (state is not null && packet.Header.FrameSequence <= state.LastFrameSequence)
                    return false;
                FixedQuantizedHumanoidPose pose = default;
                if (packet.Header.Kind == HumanoidPosePacketKind.Baseline)
                {
                    if (!TryAcceptBaseline(packet, (ushort)connection.ServerPlayerIndex, out pose))
                        return false;
                }
                else if (state is null || state.BaselineLength == 0
                    || packet.Header.BaselineSequence != state.BaselineSequence
                    || !HumanoidPoseCodec.TryReadDeltaAvatar(packet.Payload, state.BaselinePose, out _, out _, out int consumed)
                    || consumed != packet.Payload.Length)
                    return false;

                long tick = _replication.CurrentServerTickId == 0 ? _replication.AdvanceServerTick() : _replication.CurrentServerTickId;
                HumanoidPosePacket.StampServerFields(bytes, connection.SessionId, tick, GetUtcSeconds(), NetworkAuthorityMode.ServerAuthoritative);
                state ??= new CanonicalPoseState(connection.SessionId, connection.ClientId);
                _canonicalPoses[connection.ServerPlayerIndex] = state;
                if (packet.Header.Kind == HumanoidPosePacketKind.Baseline)
                {
                    bytes.CopyTo(state.Baseline);
                    state.BaselineLength = bytes.Length;
                    state.BaselinePose = pose;
                    state.BaselineSequence = packet.Header.BaselineSequence;
                    state.DeltaLength = 0;
                }
                else
                {
                    bytes.CopyTo(state.LatestDelta);
                    state.DeltaLength = bytes.Length;
                }
                state.LastFrameSequence = packet.Header.FrameSequence;
                connection.LastHeardUtc = DateTime.UtcNow;
                return true;
            }
        }
    }

    /// <summary>Materializes retained baseline and delta packets only for session synchronization.</summary>
    public bool TryGetLateJoinHumanoidPoseFrames(Guid sessionId, string clientId, out HumanoidPoseFrame[] frames)
    {
        frames = [];
        lock (_poseReplicationLock)
            foreach (CanonicalPoseState state in _canonicalPoses.Values)
            {
                if (state.SessionId != sessionId || state.ClientId != clientId || state.BaselineLength == 0
                    || !HumanoidPosePacket.TryRead(state.Baseline.AsSpan(0, state.BaselineLength), out HumanoidPosePacketView baseline))
                    continue;
                frames = state.DeltaLength != 0 && HumanoidPosePacket.TryRead(state.LatestDelta.AsSpan(0, state.DeltaLength), out HumanoidPosePacketView delta)
                    ? [baseline.ToFrame(), delta.ToFrame()] : [baseline.ToFrame()];
                return true;
            }
        return false;
    }

    public void ForgetCanonicalPose(int serverPlayerIndex)
    {
        lock (_poseReplicationLock)
            _canonicalPoses.Remove(serverPlayerIndex);
    }

    public void ClearCanonicalPoses()
    {
        lock (_poseReplicationLock)
            _canonicalPoses.Clear();
    }

    private void DisposePoseQueue()
    {
        lock (_poseQueueLock)
        {
            _poseQueueDisposed = true;
            while (_poseQueue.TryDequeue(out var item))
                _poseSlabs.Return(item.Slab);
        }
    }

    private static bool TryPreflightManagedHumanoidPoseFrame(NetworkPlayerConnection connection, in HumanoidPosePacketView packet)
    {
        if (connection.ServerPlayerIndex is <= 0 or > ushort.MaxValue
            || packet.Header.SessionId != connection.SessionId || !packet.SourceClientIdEquals(connection.ClientId)
            || packet.Header.Channel != NetworkReplicationChannel.HumanoidPose
            || packet.Header.AuthorityMode != NetworkAuthorityMode.ClientPredicted
            || packet.Header.ServerTickId != 0 || packet.Header.ServerTimestampUtc != 0
            || packet.Header.AvatarCount != 1 || packet.EntityIdCount != 1
            || packet.GetEntityId(0) != connection.NetworkEntityId || packet.Header.FrameSequence == 0 || packet.Payload.IsEmpty)
            return false;
        ushort avatarId = (ushort)connection.ServerPlayerIndex;
        if (packet.Header.Kind == HumanoidPosePacketKind.Baseline)
            return TryAcceptBaseline(packet, avatarId, out _);
        return packet.Header.BaselineSequence != 0
            && HumanoidPoseCodec.TryReadDeltaAvatar(packet.Payload, default(FixedQuantizedHumanoidPose), out HumanoidPoseAvatarHeader header, out _, out int consumed)
            && consumed == packet.Payload.Length && header.EntityId == avatarId
            && (header.Flags & HumanoidPoseFlags.Baseline) == 0 && HasOnlyKnownPoseFlags(header.Flags);
    }

    private static bool TryAcceptBaseline(in HumanoidPosePacketView packet, ushort entityId, out FixedQuantizedHumanoidPose pose)
        => HumanoidPoseCodec.TryReadBaselineAvatarFixed(packet.Payload, out HumanoidPoseAvatarHeader header, out pose, out int consumed)
            && consumed == packet.Payload.Length && header.EntityId == entityId
            && (header.Flags & HumanoidPoseFlags.Baseline) != 0 && HasOnlyKnownPoseFlags(header.Flags)
            && header.Sequence != 0 && header.Sequence == packet.Header.BaselineSequence;

    private static bool HasOnlyKnownPoseFlags(HumanoidPoseFlags flags)
    {
        const HumanoidPoseFlags KnownFlags = HumanoidPoseFlags.Hip
            | HumanoidPoseFlags.Head
            | HumanoidPoseFlags.LeftHand
            | HumanoidPoseFlags.RightHand
            | HumanoidPoseFlags.LeftFoot
            | HumanoidPoseFlags.RightFoot
            | HumanoidPoseFlags.LodMask
            | HumanoidPoseFlags.Baseline
            | HumanoidPoseFlags.RootChanged
            | HumanoidPoseFlags.ForceResend;
        return (flags & ~KnownFlags) == 0;
    }

    private sealed class CanonicalPoseState(Guid sessionId, string clientId)
    {
        public Guid SessionId { get; } = sessionId;
        public string ClientId { get; } = clientId;
        public byte[] Baseline { get; } = new byte[HumanoidPosePacket.MaxPacketBytes];
        public byte[] LatestDelta { get; } = new byte[HumanoidPosePacket.MaxPacketBytes];
        public int BaselineLength { get; set; }
        public int DeltaLength { get; set; }
        public FixedQuantizedHumanoidPose BaselinePose { get; set; }
        public ushort BaselineSequence { get; set; }
        public uint LastFrameSequence { get; set; }
    }
}
