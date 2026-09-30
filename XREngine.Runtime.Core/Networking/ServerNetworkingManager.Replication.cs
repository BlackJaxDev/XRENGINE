using System.Net;
using System.Security.Cryptography;
using MemoryPack;
using XREngine.Networking;

namespace XREngine;

public partial class ServerNetworkingManager
{
    private void DisposeServerReplication()
    {
        NetworkPlayerConnection[] connections;
        lock (_playerLock)
        {
            connections = _playersByIndex.Values.Concat(_resumeByClientKey.Values).Distinct().ToArray();
            foreach (NetworkPlayerConnection connection in connections)
                _replication.ForgetPlayer(connection.SessionId, connection.ServerPlayerIndex);
            _playersByIndex.Clear();
            _playersByClientId.Clear();
            _resumeByClientKey.Clear();
        }
        lock (_replicationTransferLock)
            _replicationTransfers.Clear();
        RuntimeNetworkingHostServices.Current.EnqueueSimulation(() =>
        {
            foreach (NetworkPlayerConnection connection in connections)
                ReleasePlayerResources(connection);
        });
    }

    private const int ReplicationChunkPayloadBytes = 5 * 1024;
    private const int ReplicationTransferRetryLimit = 4;
    private static readonly TimeSpan ReplicationTransferRetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReplicationCaptureInterval = TimeSpan.FromMilliseconds(50);
    private readonly object _replicationTransferLock = new();
    private readonly Dictionary<int, PeerReplicationTransfer> _replicationTransfers = [];
    private bool _replicationCaptureQueued;
    private long _lastReplicationCaptureTick = -1;
    private DateTime _lastReplicationCaptureUtc;
    public string? LastReplicationFailure { get; private set; }

    /// <summary>Raised only after the client has applied and acknowledged a validated baseline.</summary>
    public event Action<ServerSessionPlayerEvent>? SynchronizationCompleted;

    /// <summary>Begins a per-peer baseline after pawn creation and admission have completed.</summary>
    public void BeginReplicationSynchronization(ServerSessionPlayerEvent player)
    {
        if (!RequiresManagedUdpTransport || player.SessionId == Guid.Empty || player.ServerPlayerIndex < 0)
            return;

        lock (_replicationTransferLock)
        {
            _replicationTransfers[player.ServerPlayerIndex] = new PeerReplicationTransfer(player.SessionId, player.ServerPlayerIndex);
            foreach (PeerReplicationTransfer existing in _replicationTransfers.Values)
                if (existing.PlayerIndex != player.ServerPlayerIndex && existing.SessionId == player.SessionId)
                    existing.ForceSideChannelUpdate = true;
            QueueReplicationCapture_NoLock();
        }
    }

    /// <summary>Forgets a departing player's transfer state; retained resume identities receive a fresh baseline on rejoin.</summary>
    public void RemoveReplicationSynchronization(ServerSessionPlayerEvent player)
    {
        if (player.ServerPlayerIndex < 0)
            return;
        ForgetCanonicalPose(player.ServerPlayerIndex);
        lock (_playerLock)
            _replication.ForgetPlayer(player.SessionId, player.ServerPlayerIndex);

        lock (_replicationTransferLock)
        {
            _replicationTransfers.Remove(player.ServerPlayerIndex);
            foreach (PeerReplicationTransfer existing in _replicationTransfers.Values)
                if (existing.SessionId == player.SessionId)
                    existing.ForceSideChannelUpdate = true;
        }
    }

    /// <summary>Called by the server send loop to retry transfers and capture a canonical simulation snapshot.</summary>
    public void PumpReplicationTransfers()
    {
        lock (_replicationTransferLock)
        {
            DateTime utcNow = DateTime.UtcNow;
            foreach (PeerReplicationTransfer transfer in _replicationTransfers.Values.ToArray())
            {
                if (transfer.Awaiting is null || utcNow - transfer.LastSentUtc < ReplicationTransferRetryInterval)
                    continue;

                if (++transfer.RetryCount > ReplicationTransferRetryLimit)
                {
                    LastReplicationFailure = "Peer did not acknowledge its replication transfer within the retry window.";
                    transfer.Failed = true;
                    continue;
                }

                SendAwaiting_NoLock(transfer, utcNow);
            }

            foreach (PeerReplicationTransfer transfer in _replicationTransfers.Values.Where(static value => value.Failed).ToArray())
            {
                if (TryGetReplicationConnection(transfer, out NetworkPlayerConnection? connection) && connection?.LastEndpoint is not null)
                    SendErrorToClient(connection.ClientId, 504, "Replication Synchronization Failed", "The server could not complete the required replication transfer.", connection.ServerPlayerIndex, fatal: true, target: connection.LastEndpoint);
                _replicationTransfers.Remove(transfer.PlayerIndex);
            }

            QueueReplicationCapture_NoLock();
        }
    }

    /// <summary>Routes Phase 6 state messages without requiring a second <c>HandleStateChange</c> override.</summary>
    public bool TryHandleReplicationStateChange(StateChangeInfo change, IPEndPoint? sender)
    {
        if (sender is null)
            return false;

        switch (change.Type)
        {
            case EStateChangeType.ReplicationTransferAck:
                if (StateChangePayloadSerializer.TryDeserialize<ReplicationTransferAck>(change.Data, out ReplicationTransferAck? ack) && ack is not null)
                    HandleReplicationAck(ack, sender);
                return true;
            case EStateChangeType.ReplicationResyncRequest:
                if (StateChangePayloadSerializer.TryDeserialize<ReplicationResyncRequest>(change.Data, out ReplicationResyncRequest? request) && request is not null)
                    HandleReplicationResyncRequest(request, sender);
                return true;
            case EStateChangeType.ReplicationSyncComplete:
                if (StateChangePayloadSerializer.TryDeserialize<ReplicationSyncComplete>(change.Data, out ReplicationSyncComplete? complete) && complete is not null)
                    HandleReplicationSyncComplete(complete, sender);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Caches an authoritative pose so late join baselines include the latest pose frame.</summary>
    public void RecordReplicationPose(HumanoidPoseFrame frame)
    {
        if (string.IsNullOrWhiteSpace(frame.SourceClientId))
            return;

        lock (_replicationTransferLock)
            foreach (PeerReplicationTransfer transfer in _replicationTransfers.Values)
                if (transfer.SessionId == frame.SessionId)
                    transfer.ForceSideChannelUpdate = true;
    }

    private void QueueReplicationCapture_NoLock()
    {
        if (_replicationCaptureQueued || _replicationTransfers.Count == 0 || TickProgress == _lastReplicationCaptureTick
            || (_lastReplicationCaptureUtc != default && DateTime.UtcNow - _lastReplicationCaptureUtc < ReplicationCaptureInterval))
            return;

        _replicationCaptureQueued = true;
        RuntimeNetworkingHostServices.Current.EnqueueSimulation(CaptureReplicationSnapshotOnSimulationThread);
    }

    private void CaptureReplicationSnapshotOnSimulationThread()
    {
        ReplicatedWorldSnapshot? snapshot = null;
        Exception? failure = null;
        try
        {
            IReplicatedEntityWorld? world;
            Guid sessionId;
            lock (_replicationTransferLock)
            {
                world = GetReplicationWorld_NoLock();
                sessionId = GetReplicationCaptureSession_NoLock();
            }
            if (world is null)
                throw new InvalidOperationException("The active runtime networking host has no replicated entity world.");

            long tick = TickProgress;
            if (sessionId == Guid.Empty)
                throw new InvalidOperationException("No active session is available for replication capture.");
            snapshot = world.CaptureSnapshot(sessionId, tick);
            if (snapshot is null)
                throw new InvalidOperationException("The replication world returned no snapshot.");
            if (!world.ValidateSnapshot(snapshot, out string? error))
                throw new InvalidOperationException(error ?? "The replication world returned an invalid snapshot.");
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        lock (_replicationTransferLock)
        {
            _replicationCaptureQueued = false;
            if (failure is not null || snapshot is null)
            {
                foreach (PeerReplicationTransfer transfer in _replicationTransfers.Values)
                    transfer.Failed = true;
                LastReplicationFailure = failure?.Message ?? "No replication snapshot.";
                Debug.NetworkingWarning("[Server] Replication snapshot capture failed: {0}", failure?.Message ?? "no snapshot");
                return;
            }

            _lastReplicationCaptureTick = snapshot.TickId;
            _lastReplicationCaptureUtc = DateTime.UtcNow;
            foreach (PeerReplicationTransfer transfer in _replicationTransfers.Values)
                try { PrepareSnapshotForPeer_NoLock(transfer, snapshot); }
                catch (Exception ex)
                {
                    transfer.Failed = true;
                    LastReplicationFailure = ex.Message;
                    Debug.NetworkingWarning("[Server] Replication transfer could not be prepared: {0}", ex.Message);
                }
        }
    }

    private void PrepareSnapshotForPeer_NoLock(PeerReplicationTransfer transfer, ReplicatedWorldSnapshot snapshot)
    {
        if (!TryGetReplicationConnection(transfer, out NetworkPlayerConnection? connection))
        {
            transfer.Failed = true;
            return;
        }
        if (connection is null)
        {
            transfer.Failed = true;
            return;
        }

        ReplicatedWorldSnapshot relevant = FilterSnapshot(snapshot, connection);
        if (!transfer.Synchronized)
        {
            if (transfer.Awaiting is not null || transfer.TransferSnapshot is not null)
            {
                // Keep one coalesced successor while the immutable baseline is in flight.
                // Full state upserts make older intermediate snapshots redundant.
                transfer.BufferedSnapshot = snapshot;
                return;
            }

            transfer.TransferId = Guid.NewGuid();
            lock (_playerLock)
                connection.SynchronizedUtc = null;
            transfer.TransferSnapshot = relevant;
            transfer.Chunks = CreateBaselineChunks(transfer, relevant, connection);
            transfer.NextChunkIndex = 0;
            SendAwaiting_NoLock(transfer, DateTime.UtcNow);
            return;
        }

        if (transfer.Awaiting is not null || transfer.AcknowledgedSnapshot is null)
        {
            transfer.BufferedSnapshot = snapshot;
            return;
        }

        transfer.BufferedSnapshot = null;

        ReplicatedWorldDelta delta = CreateDelta(transfer.AcknowledgedSnapshot, relevant);
        if (delta.Upserts.Length == 0 && delta.DestroyedEntityIds.Length == 0 && !transfer.ForceSideChannelUpdate)
            return;

        ReplicationDeltaBatch pendingDelta = new()
        {
            SessionId = transfer.SessionId,
            ConnectionGeneration = connection.ReplicationConnectionGeneration,
            CredentialEpoch = connection.CredentialEpoch,
            TransferId = transfer.TransferId,
            Sequence = ++transfer.NextDeltaSequence,
            World = delta,
            Roster = CreateRoster(connection.SessionId),
            Leases = CreateLeases(connection.SessionId),
            Poses = CreatePoses(connection.SessionId),
            Transforms = CreateTransforms(connection.SessionId),
        };
        // UDP has no fragmentation guarantee. A large change recovers through the already
        // byte-chunked baseline path instead of silently dropping an oversized delta.
        if (StateChangePayloadSerializer.Serialize(pendingDelta).Length > ReplicationChunkPayloadBytes)
        {
            transfer.ResetForBaseline();
            _lastReplicationCaptureTick = -1;
            QueueReplicationCapture_NoLock();
            return;
        }
        transfer.PendingDelta = pendingDelta;
        transfer.ForceSideChannelUpdate = false;
        transfer.NextSnapshot = relevant;
        SendAwaiting_NoLock(transfer, DateTime.UtcNow);
    }

    private IReplicatedEntityWorld? GetReplicationWorld_NoLock()
    {
        foreach (PeerReplicationTransfer transfer in _replicationTransfers.Values)
            if (TryGetReplicationConnection(transfer, out NetworkPlayerConnection? connection)
                && connection?.WorldContext?.ReplicatedEntityWorld is { } world)
            {
                return world;
            }

        return null;
    }

    private Guid GetReplicationCaptureSession_NoLock()
        => _replicationTransfers.Values.FirstOrDefault()?.SessionId ?? Guid.Empty;

    private void HandleReplicationAck(ReplicationTransferAck ack, IPEndPoint sender)
    {
        lock (_replicationTransferLock)
        {
            PeerReplicationTransfer? transfer = FindAuthenticatedTransfer_NoLock(ack.SessionId, ack.ConnectionGeneration, ack.CredentialEpoch, ack.TransferId, sender);
            if (transfer is null)
                return;

            if (ack.DeltaSequence != 0)
            {
                if (transfer.PendingDelta is null || ack.DeltaSequence != transfer.PendingDelta.Sequence)
                    return;

                transfer.AcknowledgedSnapshot = transfer.NextSnapshot;
                transfer.NextSnapshot = null;
                transfer.PendingDelta = null;
                transfer.Awaiting = null;
                transfer.RetryCount = 0;
                if (transfer.BufferedSnapshot is { } buffered)
                    PrepareSnapshotForPeer_NoLock(transfer, buffered);
                return;
            }

            if (transfer.Chunks is null || ack.SnapshotTickId != transfer.TransferSnapshot?.TickId || ack.ChunkIndex != transfer.NextChunkIndex)
                return;

            ++transfer.NextChunkIndex;
            transfer.Awaiting = null;
            transfer.RetryCount = 0;
            if (transfer.NextChunkIndex < transfer.Chunks.Length)
                SendAwaiting_NoLock(transfer, DateTime.UtcNow);
        }
    }

    private void HandleReplicationResyncRequest(ReplicationResyncRequest request, IPEndPoint sender)
    {
        lock (_replicationTransferLock)
        {
            PeerReplicationTransfer? transfer = FindAuthenticatedTransfer_NoLock(request.SessionId, request.ConnectionGeneration, request.CredentialEpoch, request.TransferId, sender);
            if (transfer is null)
                return;

            transfer.ResetForBaseline();
            QueueReplicationCapture_NoLock();
        }
    }

    private void HandleReplicationSyncComplete(ReplicationSyncComplete complete, IPEndPoint sender)
    {
        ServerSessionPlayerEvent? completed = null;
        IPEndPoint? confirmationTarget = null;
        lock (_replicationTransferLock)
        {
            PeerReplicationTransfer? transfer = FindAuthenticatedTransfer_NoLock(complete.SessionId, complete.ConnectionGeneration, complete.CredentialEpoch, complete.TransferId, sender);
            if (transfer is null)
            {
                return;
            }

            if (transfer.Synchronized && complete.SnapshotTickId == transfer.AcknowledgedSnapshot?.TickId)
            {
                if (TryGetReplicationConnection(transfer, out NetworkPlayerConnection? existing) && existing is not null)
                    confirmationTarget = existing.LastEndpoint;
            }
            else
            {
                if (transfer.Chunks is null || transfer.NextChunkIndex != transfer.Chunks.Length
                    || complete.SnapshotTickId != transfer.TransferSnapshot?.TickId)
                    return;

                transfer.Synchronized = true;
                transfer.AcknowledgedSnapshot = transfer.TransferSnapshot;
                transfer.TransferSnapshot = null;
                transfer.Chunks = null;
                transfer.Awaiting = null;
                transfer.RetryCount = 0;
                if (TryGetReplicationConnection(transfer, out NetworkPlayerConnection? connection) && connection is not null)
                {
                    connection.SynchronizedUtc = DateTimeOffset.UtcNow;
                    completed = CreatePlayerEvent(connection);
                    confirmationTarget = connection.LastEndpoint;
                }
            }
        }

        if (completed is not null)
            SynchronizationCompleted?.Invoke(completed);
        if (confirmationTarget is not null)
            SendStateChangeTo(confirmationTarget, EStateChangeType.ReplicationSyncComplete, complete, compress: true, resendOnFailedAck: false);
    }

    private PeerReplicationTransfer? FindAuthenticatedTransfer_NoLock(Guid sessionId, Guid connectionGeneration, long credentialEpoch, Guid transferId, IPEndPoint sender)
    {
        foreach (PeerReplicationTransfer transfer in _replicationTransfers.Values)
        {
            if (transfer.SessionId != sessionId || transfer.TransferId != transferId || !TryGetReplicationConnection(transfer, out NetworkPlayerConnection? connection))
                continue;
            if (connection is not null && IsAuthenticatedSender(connection, sender, sessionId)
                && connection.ReplicationConnectionGeneration == connectionGeneration
                && connection.CredentialEpoch == credentialEpoch)
                return transfer;
        }

        return null;
    }

    private bool TryGetReplicationConnection(PeerReplicationTransfer transfer, out NetworkPlayerConnection? connection)
    {
        lock (_playerLock)
            return _playersByIndex.TryGetValue(transfer.PlayerIndex, out connection)
                && connection.SessionId == transfer.SessionId;
    }

    private void SendAwaiting_NoLock(PeerReplicationTransfer transfer, DateTime nowUtc)
    {
        if (!TryGetReplicationConnection(transfer, out NetworkPlayerConnection? connection) || connection?.LastEndpoint is null)
        {
            transfer.Failed = true;
            return;
        }

        if (transfer.PendingDelta is { } delta)
        {
            int deltaByteCost = StateChangePayloadSerializer.Serialize(delta).Length;
            connection.Budget ??= new NetworkBandwidthBudget(MultiplayerRuntimePolicy.DefaultReplicationBytesPerSecond, GetUtcSeconds());
            if (!connection.Budget.TryConsume(deltaByteCost, GetUtcSeconds()))
                return;
            SendStateChangeTo(connection.LastEndpoint, EStateChangeType.ReplicationDeltaBatch, delta, compress: true, resendOnFailedAck: false);
            transfer.Awaiting = delta;
            transfer.LastSentUtc = nowUtc;
            return;
        }

        if (transfer.Chunks is null || transfer.NextChunkIndex >= transfer.Chunks.Length)
            return;
        ReplicationBaselineChunk chunk = transfer.Chunks[transfer.NextChunkIndex];
        int byteCost = StateChangePayloadSerializer.Serialize(chunk).Length;
        connection.Budget ??= new NetworkBandwidthBudget(MultiplayerRuntimePolicy.DefaultReplicationBytesPerSecond, GetUtcSeconds());
        if (!connection.Budget.TryConsume(byteCost, GetUtcSeconds()))
            return;

        SendStateChangeTo(connection.LastEndpoint, EStateChangeType.ReplicationBaselineChunk, chunk, compress: true, resendOnFailedAck: false);
        transfer.Awaiting = chunk;
        transfer.LastSentUtc = nowUtc;
    }

    private ReplicationBaselineChunk[] CreateBaselineChunks(PeerReplicationTransfer transfer, ReplicatedWorldSnapshot snapshot, NetworkPlayerConnection connection)
    {
        var complete = new ReplicatedSessionSnapshot
        {
            World = snapshot,
            Roster = CreateRoster(transfer.SessionId),
            Leases = CreateLeases(transfer.SessionId),
            Poses = CreatePoses(transfer.SessionId),
            Transforms = CreateTransforms(transfer.SessionId),
        };
        byte[] serializedSnapshot = MemoryPackSerializer.Serialize(complete);
        if (serializedSnapshot.Length > 4 * 1024 * 1024)
            throw new InvalidOperationException("Replicated session baseline exceeds the 4 MiB transfer limit.");
        byte[] snapshotHash = SHA256.HashData(serializedSnapshot);
        int chunkCount = Math.Max(1, (serializedSnapshot.Length + ReplicationChunkPayloadBytes - 1) / ReplicationChunkPayloadBytes);
        ReplicationBaselineChunk[] chunks = new ReplicationBaselineChunk[chunkCount];
        for (int chunkIndex = 0; chunkIndex < chunkCount; ++chunkIndex)
        {
            int sourceIndex = chunkIndex * ReplicationChunkPayloadBytes;
            int byteCount = Math.Min(ReplicationChunkPayloadBytes, serializedSnapshot.Length - sourceIndex);
            byte[] payload = new byte[byteCount];
            serializedSnapshot.AsSpan(sourceIndex, byteCount).CopyTo(payload);
            chunks[chunkIndex] = new ReplicationBaselineChunk
            {
                SessionId = transfer.SessionId,
                ConnectionGeneration = connection.ReplicationConnectionGeneration,
                CredentialEpoch = connection.CredentialEpoch,
                TransferId = transfer.TransferId,
                SnapshotTickId = snapshot.TickId,
                ChunkIndex = (uint)chunkIndex,
                ChunkCount = (uint)chunkCount,
                TotalEntityCount = snapshot.Entities.Length,
                TotalPayloadBytes = serializedSnapshot.Length,
                SnapshotHash = snapshotHash,
                SchemaFingerprint = NetworkReplicationSchemaRegistry.Fingerprint,
                WorldAsset = connection.WorldAsset,
                Payload = new ReplicationBaselinePayload { Bytes = payload },
            };
        }

        return chunks;
    }

    private static ReplicatedWorldSnapshot FilterSnapshot(ReplicatedWorldSnapshot snapshot, NetworkPlayerConnection connection)
    {
        Dictionary<NetworkEntityId, ReplicatedEntityState> entities = new(snapshot.Entities.Length);
        foreach (ReplicatedEntityState entity in snapshot.Entities)
            if (!entity.EntityId.IsEmpty)
                entities[entity.EntityId] = entity;

        HashSet<NetworkEntityId> included = [];
        foreach (ReplicatedEntityState entity in snapshot.Entities)
            if (entity.Relevance is null || entity.Relevance.Contains(connection.RelevanceCenter))
                included.Add(entity.EntityId);

        bool changed;
        do
        {
            changed = false;
            foreach (NetworkEntityId id in included.ToArray())
            {
                if (!entities.TryGetValue(id, out ReplicatedEntityState? entity))
                    continue;
                changed |= IncludeClosure(entity.ParentId, entities, included);
                foreach (ReplicatedComponentState component in entity.Components)
                    foreach (NetworkEntityId reference in component.EntityReferences)
                        changed |= IncludeClosure(reference, entities, included);
            }
        } while (changed);

        return new ReplicatedWorldSnapshot
        {
            SessionId = connection.SessionId,
            TickId = snapshot.TickId,
            RequiredScenes = snapshot.RequiredScenes,
            RequiredSceneIds = snapshot.RequiredSceneIds,
            RequiredSchemas = snapshot.RequiredSchemas,
            Entities = snapshot.Entities.Where(entity => included.Contains(entity.EntityId)).OrderBy(entity => entity.EntityId.Value).ToArray(),
        };
    }

    private static bool IncludeClosure(NetworkEntityId? id, Dictionary<NetworkEntityId, ReplicatedEntityState> entities, HashSet<NetworkEntityId> included)
        => id is { IsEmpty: false } value && entities.ContainsKey(value) && included.Add(value);

    private static ReplicatedWorldDelta CreateDelta(ReplicatedWorldSnapshot previous, ReplicatedWorldSnapshot current)
    {
        Dictionary<NetworkEntityId, ReplicatedEntityState> oldEntities = previous.Entities.ToDictionary(entity => entity.EntityId);
        List<ReplicatedEntityState> upserts = [];
        foreach (ReplicatedEntityState currentEntity in current.Entities)
        {
            if (!oldEntities.Remove(currentEntity.EntityId, out ReplicatedEntityState? oldEntity) || !Equivalent(oldEntity, currentEntity))
                upserts.Add(currentEntity);
        }

        return new ReplicatedWorldDelta
        {
            SessionId = current.SessionId,
            TickId = current.TickId,
            BaseTickId = previous.TickId,
            Upserts = upserts.ToArray(),
            DestroyedEntityIds = oldEntities.Keys.OrderBy(id => id.Value).ToArray(),
        };
    }

    private static bool Equivalent(ReplicatedEntityState left, ReplicatedEntityState right)
    {
        if (left.EntityId != right.EntityId || left.ParentId != right.ParentId || left.FactoryId != right.FactoryId
            || left.SchemaVersion != right.SchemaVersion || left.Name != right.Name || left.Active != right.Active
            || left.Translation != right.Translation || left.Rotation != right.Rotation || left.Scale != right.Scale
            || left.SourcePath != right.SourcePath || !Equivalent(left.Relevance, right.Relevance)
            || left.Components.Length != right.Components.Length)
        {
            return false;
        }

        for (int index = 0; index < left.Components.Length; ++index)
        {
            ReplicatedComponentState a = left.Components[index];
            ReplicatedComponentState b = right.Components[index];
            if (a.ComponentId != b.ComponentId || a.SchemaId != b.SchemaId || a.SchemaVersion != b.SchemaVersion
                || a.Active != b.Active
                || !a.Payload.AsSpan().SequenceEqual(b.Payload)
                || !a.EntityReferences.AsSpan().SequenceEqual(b.EntityReferences)
                || !a.AssetReferences.AsSpan().SequenceEqual(b.AssetReferences))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Equivalent(NetworkRelevanceHint? left, NetworkRelevanceHint? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null)
            return false;
        return left.EntityId == right.EntityId
            && left.Center == right.Center
            && left.Radius == right.Radius
            && left.Tags.AsSpan().SequenceEqual(right.Tags);
    }

    private ReplicationRosterEntry[] CreateRoster(Guid sessionId)
    {
        lock (_playerLock)
            return _playersByIndex.Values.Where(player => player.SessionId == sessionId).Select(player => new ReplicationRosterEntry
            {
                SessionId = player.SessionId,
                ClientId = player.ClientId,
                ServerPlayerIndex = player.ServerPlayerIndex,
                PlayerEntityId = player.NetworkEntityId,
                TransformId = player.TransformId,
                DisplayName = player.JoinRequest?.DisplayName,
            }).ToArray();
    }

    private NetworkAuthorityLease[] CreateLeases(Guid sessionId)
    {
        lock (_playerLock)
            return _replication.GetLeases(sessionId);
    }

    private HumanoidPoseFrame[] CreatePoses(Guid sessionId)
    {
        lock (_playerLock)
        {
            List<HumanoidPoseFrame> frames = [];
            foreach (NetworkPlayerConnection player in _playersByIndex.Values)
            {
                if (player.SessionId != sessionId)
                    continue;
                if (TryGetLateJoinHumanoidPoseFrames(sessionId, player.ClientId, out HumanoidPoseFrame[] current))
                    frames.AddRange(current);
            }
            return frames.ToArray();
        }
    }

    private PlayerTransformUpdate[] CreateTransforms(Guid sessionId)
    {
        lock (_playerLock)
            return _playersByIndex.Values.Where(player => player.SessionId == sessionId && player.LastTransform is not null)
                .Select(player => player.LastTransform!).ToArray();
    }

    private sealed class PeerReplicationTransfer(Guid sessionId, int playerIndex)
    {
        public Guid SessionId { get; } = sessionId;
        public int PlayerIndex { get; } = playerIndex;
        public Guid TransferId { get; set; }
        public ReplicatedWorldSnapshot? TransferSnapshot { get; set; }
        public ReplicatedWorldSnapshot? AcknowledgedSnapshot { get; set; }
        public ReplicatedWorldSnapshot? NextSnapshot { get; set; }
        public ReplicatedWorldSnapshot? BufferedSnapshot { get; set; }
        public ReplicationBaselineChunk[]? Chunks { get; set; }
        public ReplicationDeltaBatch? PendingDelta { get; set; }
        public object? Awaiting { get; set; }
        public uint NextChunkIndex { get; set; }
        public uint NextDeltaSequence { get; set; }
        public int RetryCount { get; set; }
        public DateTime LastSentUtc { get; set; }
        public bool Synchronized { get; set; }
        public bool Failed { get; set; }
        public bool ForceSideChannelUpdate { get; set; }

        public void ResetForBaseline()
        {
            TransferId = Guid.Empty;
            TransferSnapshot = null;
            AcknowledgedSnapshot = null;
            NextSnapshot = null;
            BufferedSnapshot = null;
            Chunks = null;
            PendingDelta = null;
            Awaiting = null;
            NextChunkIndex = 0;
            NextDeltaSequence = 0;
            RetryCount = 0;
            Synchronized = false;
        }
    }
}
