using System.Buffers.Binary;
using System.Net;
using XREngine.Networking;

namespace XREngine;

public partial class ClientNetworkingManager
{
    private readonly DeferredRealtimePacketQueue _receivedPosePackets = new(HighRateRingCapacity, HumanoidPosePacket.MaxPacketBytes);
    private readonly Action _drainPosePackets;
    private bool _poseDrainQueued;

    /// <summary>Copies a pose into bounded storage before the receive buffer is reused.</summary>
    private void QueueHumanoidPosePacket(ReadOnlySpan<byte> payload, IPEndPoint? sender)
    {
        if (!HumanoidPosePacket.TryRead(payload, out _))
        {
            RecordPayloadDecodeFailure();
            return;
        }

        lock (_replicationSyncLock)
        {
            if (_replicationDisposed || !IsGameplayReady)
                return;
            if (!_receivedPosePackets.TryEnqueue(payload, sender, _replicationAttemptGeneration))
            {
                RecordPoseSlabExhausted();
                return;
            }
            if (_poseDrainQueued)
                return;
            _poseDrainQueued = true;
        }
        RuntimeNetworkingHostServices.Current.EnqueueSimulation(_drainPosePackets);
    }

    /// <summary>Applies only poses belonging to the current world synchronization attempt.</summary>
    private void DrainHumanoidPosePackets()
    {
        lock (_replicationSyncLock)
        {
            try
            {
                while (_receivedPosePackets.TryDequeue(out PersistentReceiveSlab slab, out _, out long attempt))
                {
                    try
                    {
                        using var measurement = PoseApplyMeasurements.Begin(slab.Length);
                        if (IsReplicationAttemptCurrent_NoLock(attempt) && IsGameplayReady
                            && HumanoidPosePacket.TryRead(slab.Span, out HumanoidPosePacketView packet))
                            RaiseHumanoidPosePacketReceived(packet);
                    }
                    catch (Exception exception)
                    {
                        Debug.NetworkingWarning("[Client] Pose dispatch failed: {0}", exception.Message);
                    }
                    finally
                    {
                        _receivedPosePackets.Return(slab);
                    }
                }
            }
            finally
            {
                _poseDrainQueued = false;
            }
        }
    }

    /// <summary>Uses the binary clock sample directly without materializing an object or client string.</summary>
    protected override void OnClockSyncReceived(in ClockSyncSample sample, ReadOnlySpan<byte> clientIdUtf8)
    {
        Span<byte> localId = stackalloc byte[ClockSyncPacket.MaxClientIdBytes];
        if (!TryEncodeUtf8(EffectiveClientId, localId, out int localIdLength)
            || !clientIdUtf8.SequenceEqual(localId[..localIdLength])
            || (_activeSessionId != Guid.Empty && sample.SessionId != _activeSessionId))
            return;

        double receiveUtc = GetUtcSeconds();
        double midpoint = (sample.ClientSendTimestampUtc + receiveUtc) * 0.5d;
        _clockOffsetSeconds = sample.ServerSendTimestampUtc - midpoint;
        _lastReceivedServerTickId = Math.Max(_lastReceivedServerTickId, sample.ServerTickId);
        base.OnClockSyncReceived(sample, clientIdUtf8);
    }

    /// <summary>Binds an outgoing pose to the assigned player without allocating entity arrays.</summary>
    protected override bool TryPrepareOutgoingHumanoidPose(
        ref HumanoidPosePacketHeader header,
        Span<byte> avatarPayload,
        Span<byte> clientIdUtf8,
        out int clientIdLength,
        Span<NetworkEntityId> entityIds,
        out int entityIdCount)
    {
        clientIdLength = 0;
        entityIdCount = 0;
        if (!IsGameplayReady || !TryEncodeUtf8(EffectiveClientId, clientIdUtf8, out clientIdLength))
            return false;

        header.SessionId = _activeSessionId;
        header.FrameSequence = ++_poseFrameSequence;
        header.AuthorityMode = NetworkAuthorityMode.ClientPredicted;
        header.Channel = NetworkReplicationChannel.HumanoidPose;
        if (IsManagedTransportRequested)
        {
            if (_primaryAssignedServerPlayerIndex is <= 0 or > ushort.MaxValue
                || _primaryAssignedEntityId.IsEmpty || header.AvatarCount != 1
                || avatarPayload.Length < 6 || header.BaselineSequence == 0 || entityIds.IsEmpty)
                return false;

            BinaryPrimitives.WriteUInt16LittleEndian(avatarPayload, (ushort)_primaryAssignedServerPlayerIndex);
            entityIds[entityIdCount++] = _primaryAssignedEntityId;
            return true;
        }

        var players = RuntimeNetworkingHostServices.Current.LocalPlayers;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i]?.PlayerInfo is not { NetworkEntityId.IsEmpty: false } info)
                continue;
            if (entityIdCount == entityIds.Length)
                return false;
            entityIds[entityIdCount++] = info.NetworkEntityId;
        }
        if (entityIdCount == 0 && !_primaryAssignedEntityId.IsEmpty)
        {
            if (entityIds.IsEmpty)
                return false;
            entityIds[entityIdCount++] = _primaryAssignedEntityId;
        }
        return true;
    }
}
