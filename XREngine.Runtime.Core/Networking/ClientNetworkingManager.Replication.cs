using System.Net;
using System.Security.Cryptography;
using MemoryPack;
using XREngine.Networking;

namespace XREngine;

public partial class ClientNetworkingManager
{
    private const int MaximumBufferedReplicationDeltas = 64;
    private const int MaximumBufferedReplicationBytes = 512 * 1024;
    private static readonly TimeSpan ReplicationSynchronizationTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ReplicationConfirmationRetryInterval = TimeSpan.FromSeconds(1);
    private readonly object _replicationSyncLock = new();
    private readonly Dictionary<uint, ReplicationBaselineChunk> _baselineChunks = [];
    private readonly SortedDictionary<uint, ReplicationDeltaBatch> _bufferedReplicationDeltas = [];
    private ClientReplicationSynchronizationState _replicationSynchronizationState = ClientReplicationSynchronizationState.NotAssigned;
    private Guid _replicationTransferId;
    private Guid _replicationConnectionGeneration;
    private long _replicationBaselineTick;
    private uint _expectedReplicationDeltaSequence = 1;
    private int _bufferedReplicationBytes;
    private long _replicationAttemptGeneration;
    private bool _replicationApplyQueued;
    private int _baselineBufferedBytes;
    private DateTime _replicationAbsoluteDeadlineUtc;
    private byte[]? _appliedReplicationHash;
    private readonly HashSet<Guid> _retiredReplicationTransfers = [];
    private readonly Dictionary<NetworkEntityId, NetworkAuthorityLease> _replicatedAuthorityLeases = [];
    public IReadOnlyList<NetworkAuthorityLease> GetReplicatedAuthorityLeases()
    {
        lock (_replicationSyncLock)
            return _replicatedAuthorityLeases.Values.Select(static lease => lease.Clone()).ToArray();
    }
    private DateTime _replicationDeadlineUtc;
    private DateTime _lastReplicationConfirmationUtc;
    private ReplicationSyncComplete? _pendingReplicationConfirmation;
    private string? _replicationFailure;
    // Bound once per assignment/baseline attempt. Delayed callbacks must never
    // resolve the current primary world again: another manager may own it by then.
    private IRuntimeNetworkWorldContext? _replicationWorldContext;
    private ReplicationWorldOwnershipLease _replicationWorldLease;
    private bool _replicationDisposed;

    /// <summary>Last synchronization rejection or recovery reason, without credentials.</summary>
    public string? ReplicationFailure { get { lock (_replicationSyncLock) return _replicationFailure; } }

    public ClientReplicationSynchronizationState ReplicationSynchronizationState
    {
        get { lock (_replicationSyncLock) return _replicationSynchronizationState; }
    }

    private bool IsReplicationDisposed
    {
        get { lock (_replicationSyncLock) return _replicationDisposed; }
    }

    /// <summary>Gameplay input and presentation become eligible only after a validated simulation-thread commit.</summary>
    public bool IsGameplayReady => HasValidLocalAssignment && (!IsManagedTransportRequested || ReplicationSynchronizationState == ClientReplicationSynchronizationState.Synchronized);

    public event Action<ClientReplicationSynchronizationState>? ReplicationSynchronizationStateChanged;

    /// <summary>Routes Phase 6 messages without requiring a second state-change override.</summary>
    public bool TryHandleReplicationStateChange(StateChangeInfo change, IPEndPoint? sender)
    {
        switch (change.Type)
        {
            case EStateChangeType.ReplicationBaselineChunk:
                if (StateChangePayloadSerializer.TryDeserialize<ReplicationBaselineChunk>(change.Data, out ReplicationBaselineChunk? chunk) && chunk is not null)
                    HandleReplicationBaselineChunk(chunk);
                return true;
            case EStateChangeType.ReplicationDeltaBatch:
                if (StateChangePayloadSerializer.TryDeserialize<ReplicationDeltaBatch>(change.Data, out ReplicationDeltaBatch? delta) && delta is not null)
                    HandleReplicationDeltaBatch(delta);
                return true;
            case EStateChangeType.ReplicationSyncComplete:
                if (StateChangePayloadSerializer.TryDeserialize<ReplicationSyncComplete>(change.Data, out ReplicationSyncComplete? confirmation) && confirmation is not null)
                    HandleReplicationSyncConfirmation(confirmation);
                return true;
            default:
                return false;
        }
    }

    internal void BeginReplicationSynchronization(PlayerAssignment assignment)
    {
        if (!IsManagedTransportRequested || IsReplicationDisposed)
            return;
        IRuntimeNetworkWorldContext? cleanup = null;
        ReplicationWorldOwnershipLease cleanupLease = default;
        lock (_replicationSyncLock)
        {
            IRuntimeNetworkWorldContext? next = ResolvePrimaryWorldInstance();
            cleanup = _replicationWorldContext;
            cleanupLease = _replicationWorldLease;
            _replicationWorldContext = next;
            _replicationWorldLease = next is null ? default : ReplicationWorldOwnershipLeases.Acquire(next.WorldInstance, this);
            ++_replicationAttemptGeneration;
            _replicationFailure = null;
            _replicationConnectionGeneration = assignment.ReplicationConnectionGeneration;
            _replicationTransferId = Guid.Empty;
            _replicationBaselineTick = 0;
            _expectedReplicationDeltaSequence = 1;
            _baselineChunks.Clear();
            _baselineBufferedBytes = 0;
            _retiredReplicationTransfers.Clear();
            _bufferedReplicationDeltas.Clear();
            _bufferedReplicationBytes = 0;
            _replicatedAuthorityLeases.Clear();
            _replicationApplyQueued = false;
            _replicationDeadlineUtc = DateTime.UtcNow + ReplicationSynchronizationTimeout;
            _replicationAbsoluteDeadlineUtc = DateTime.UtcNow.AddSeconds(64);
            _appliedReplicationHash = null;
            _pendingReplicationConfirmation = null;
            SetReplicationState_NoLock(ClientReplicationSynchronizationState.Synchronizing);
            QueueReplicationWorldPause_NoLock(_replicationAttemptGeneration, _replicationWorldContext);
        }
        QueueReplicationWorldCleanup(cleanup, cleanupLease, pause: true);
    }

    internal void PauseUntilReplicationAssignment()
    {
        if (!IsManagedTransportRequested || IsReplicationDisposed)
            return;
        lock (_replicationSyncLock)
        {
            ++_replicationAttemptGeneration;
            SetReplicationState_NoLock(ClientReplicationSynchronizationState.NotAssigned);
            QueueReplicationWorldPause_NoLock(_replicationAttemptGeneration, ResolvePrimaryWorldInstance());
        }
    }

    internal void ResetReplicationSynchronization()
    {
        IRuntimeNetworkWorldContext? cleanup;
        ReplicationWorldOwnershipLease cleanupLease;
        lock (_replicationSyncLock)
        {
            ++_replicationAttemptGeneration;
            cleanup = _replicationWorldContext;
            cleanupLease = _replicationWorldLease;
            _replicationWorldContext = null;
            _replicationWorldLease = default;
            _replicationTransferId = Guid.Empty;
            _replicationBaselineTick = 0;
            _expectedReplicationDeltaSequence = 1;
            _baselineChunks.Clear();
            _baselineBufferedBytes = 0;
            _bufferedReplicationDeltas.Clear();
            _bufferedReplicationBytes = 0;
            _replicatedAuthorityLeases.Clear();
            _replicationApplyQueued = false;
            _pendingReplicationConfirmation = null;
            SetReplicationState_NoLock(_assignmentReceived ? ClientReplicationSynchronizationState.Synchronizing : ClientReplicationSynchronizationState.NotAssigned);
        }

        QueueReplicationWorldCleanup(cleanup, cleanupLease, pause: true);
    }

    private void HandleReplicationBaselineChunk(ReplicationBaselineChunk chunk)
    {
        long attempt;
        bool apply = false;
        lock (_replicationSyncLock)
        {
            if (!MatchesReplicationAssignment_NoLock(chunk.SessionId, chunk.ConnectionGeneration, chunk.CredentialEpoch)
                || chunk.TransferId == Guid.Empty || chunk.ChunkCount == 0 || chunk.ChunkIndex >= chunk.ChunkCount)
            {
                return;
            }

            if (_replicationTransferId != Guid.Empty && _replicationTransferId != chunk.TransferId)
            {
                if (_retiredReplicationTransfers.Contains(chunk.TransferId))
                    return;
                // The authoritative server may replace an oversized delta with a fresh baseline.
                if (chunk.ChunkIndex != 0 || chunk.SnapshotTickId <= _replicationBaselineTick)
                    return;
                _retiredReplicationTransfers.Add(_replicationTransferId);
                if (_retiredReplicationTransfers.Count > 64)
                {
                    FailReplication_NoLock("Replication restarted too many times in one connection.");
                    return;
                }
                ++_replicationAttemptGeneration;
                _replicationTransferId = Guid.Empty;
                _baselineChunks.Clear();
                _baselineBufferedBytes = 0;
                _bufferedReplicationDeltas.Clear();
                _bufferedReplicationBytes = 0;
                _expectedReplicationDeltaSequence = 1;
                _replicationApplyQueued = false;
                QueueReplicationWorldPause_NoLock(_replicationAttemptGeneration, _replicationWorldContext);
            }

            if (_baselineChunks.TryGetValue(chunk.ChunkIndex, out ReplicationBaselineChunk? existing))
            {
                if (!BaselineChunksEqual(existing, chunk))
                    FailReplication_NoLock("Conflicting duplicate replication chunk.");
                else
                    SendReplicationAck(chunk, 0);
                return;
            }
            if (_appliedReplicationHash is not null && chunk.TransferId == _replicationTransferId
                && _replicationSynchronizationState is ClientReplicationSynchronizationState.AwaitingServerConfirmation or ClientReplicationSynchronizationState.Synchronized)
            {
                if (!CryptographicOperations.FixedTimeEquals(_appliedReplicationHash, chunk.SnapshotHash))
                    FailReplication_NoLock("A completed baseline was retransmitted with different content.");
                else
                    SendReplicationAck(chunk, 0);
                return;
            }

            if (_replicationTransferId == Guid.Empty)
            {
                if (chunk.TotalPayloadBytes <= 0 || chunk.TotalPayloadBytes > 4 * 1024 * 1024
                    || chunk.ChunkCount > 1024 || chunk.TotalEntityCount < 0 || chunk.TotalEntityCount > 4096
                    || chunk.SnapshotHash.Length != 32
                    || string.IsNullOrWhiteSpace(chunk.SchemaFingerprint)
                    || !string.Equals(chunk.SchemaFingerprint, NetworkReplicationSchemaRegistry.Fingerprint, StringComparison.Ordinal)
                    || chunk.WorldAsset is null
                    || LocalWorldAsset is { } localWorldAsset && !localWorldAsset.IsSameAssetAs(chunk.WorldAsset))
                {
                    FailReplication_NoLock("Replication baseline header is unsupported or exceeds local limits.");
                    return;
                }
                _replicationTransferId = chunk.TransferId;
                if (_replicationWorldContext is null)
                {
                    _replicationWorldContext = ResolvePrimaryWorldInstance();
                    _replicationWorldLease = _replicationWorldContext is null
                        ? default
                        : ReplicationWorldOwnershipLeases.Acquire(_replicationWorldContext.WorldInstance, this);
                }
                _replicationBaselineTick = chunk.SnapshotTickId;
                _replicationAbsoluteDeadlineUtc = DateTime.UtcNow.AddSeconds(64);
                _appliedReplicationHash = null;
                SetReplicationState_NoLock(ClientReplicationSynchronizationState.Synchronizing);
            }
            else if (_replicationBaselineTick != chunk.SnapshotTickId || _baselineChunks.Count > 0 && !HasMatchingBaselineHeader(_baselineChunks.Values.First(), chunk))
            {
                FailReplication_NoLock("Replication baseline header changed during transfer.");
                return;
            }

            if (chunk.Payload?.Bytes is not { Length: > 0 and <= 5120 } bytes || _baselineBufferedBytes + bytes.Length > chunk.TotalPayloadBytes)
            {
                FailReplication_NoLock("Replication chunk exceeds the declared transfer budget.");
                return;
            }
            _baselineBufferedBytes += bytes.Length;
            _replicationDeadlineUtc = DateTime.UtcNow + ReplicationSynchronizationTimeout;

            _baselineChunks[chunk.ChunkIndex] = chunk;
            SendReplicationAck(chunk, 0);
            if (_baselineChunks.Count != chunk.ChunkCount || _replicationApplyQueued)
                return;

            _replicationApplyQueued = true;
            attempt = _replicationAttemptGeneration;
            SetReplicationState_NoLock(ClientReplicationSynchronizationState.ApplyingBaseline);
            apply = true;
        }

        if (apply)
            RuntimeNetworkingHostServices.Current.EnqueueSimulation(() => ApplyBaselineOnSimulationThread(attempt));
    }

    private void ApplyBaselineOnSimulationThread(long attempt)
    {
        ReplicatedWorldSnapshot? snapshot = null;
        ReplicationBaselineChunk? header = null;
        ReplicatedSessionSnapshot? complete = null;
        lock (_replicationSyncLock)
        {
            if (!IsReplicationAttemptCurrent_NoLock(attempt) || _baselineChunks.Count == 0)
                return;

            header = _baselineChunks.Values.OrderBy(chunk => chunk.ChunkIndex).First();
            if (_baselineChunks.Count != header.ChunkCount)
                return;
            byte[] serializedSnapshot = new byte[header.TotalPayloadBytes];
            int destination = 0;
            foreach (ReplicationBaselineChunk chunk in _baselineChunks.OrderBy(pair => pair.Key).Select(pair => pair.Value))
            {
                byte[] bytes = chunk.Payload?.Bytes ?? [];
                if (destination + bytes.Length > serializedSnapshot.Length)
                {
                    FailReplication_NoLock("Replication baseline payload totals are inconsistent.");
                    return;
                }
                bytes.CopyTo(serializedSnapshot, destination);
                destination += bytes.Length;
            }
            if (destination != serializedSnapshot.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(serializedSnapshot), header.SnapshotHash))
            {
                FailReplication_NoLock("Replication baseline hash validation failed.");
                return;
            }
            try { complete = MemoryPackSerializer.Deserialize<ReplicatedSessionSnapshot>(serializedSnapshot); }
            catch (Exception ex)
            {
                FailReplication_NoLock($"Replication baseline decoding failed: {ex.Message}");
                return;
            }
            snapshot = complete?.World;
            if (snapshot is null || snapshot.SessionId != header.SessionId || snapshot.TickId != header.SnapshotTickId || snapshot.Entities.Length != header.TotalEntityCount)
            {
                FailReplication_NoLock("Replication baseline identity validation failed.");
                return;
            }
        }

        lock (_replicationSyncLock)
        {
            if (!IsReplicationAttemptCurrent_NoLock(attempt))
                return;
            IReplicatedEntityWorld? world = _replicationWorldContext?.ReplicatedEntityWorld;
            if (world is null)
            {
                FailReplication_NoLock("The client has no replicated entity world.");
                return;
            }
            if (!world.ValidateSnapshot(snapshot, out string? error))
            {
                FailReplication_NoLock(error ?? "Replication baseline validation failed.");
                return;
            }
            if (!world.ApplySnapshot(snapshot, out error))
            {
                FailReplication_NoLock(error ?? "Replication baseline application failed.");
                return;
            }

            ApplyBaselinePresence_NoLock(complete!);
            _appliedReplicationHash = header!.SnapshotHash.ToArray();
            _baselineChunks.Clear();
            _baselineBufferedBytes = 0;
            _replicationApplyQueued = false;
            SetReplicationState_NoLock(ClientReplicationSynchronizationState.AwaitingServerConfirmation);

        }
        SendReplicationComplete(header!);
        QueueNextReplicationDelta(attempt);
    }

    private void HandleReplicationDeltaBatch(ReplicationDeltaBatch delta)
    {
        long attempt;
        lock (_replicationSyncLock)
        {
            if (!MatchesReplicationAssignment_NoLock(delta.SessionId, delta.ConnectionGeneration, delta.CredentialEpoch)
                || delta.TransferId == Guid.Empty || delta.TransferId != _replicationTransferId)
            {
                return;
            }

            if (_replicationSynchronizationState is not (ClientReplicationSynchronizationState.Synchronized or ClientReplicationSynchronizationState.AwaitingServerConfirmation))
                return;

            if (delta.Sequence < _expectedReplicationDeltaSequence)
            {
                SendReplicationAck(null, delta.Sequence);
                return;
            }
            if (delta.Sequence != _expectedReplicationDeltaSequence || delta.World.BaseTickId != _replicationBaselineTick)
            {
                FailReplication_NoLock("Replication delta sequence or baseline is out of order.");
                return;
            }

            int byteCount = StateChangePayloadSerializer.Serialize(delta).Length;
            if (_bufferedReplicationDeltas.Count >= MaximumBufferedReplicationDeltas || _bufferedReplicationBytes + byteCount > MaximumBufferedReplicationBytes)
            {
                FailReplication_NoLock("Replication delta buffer limit exceeded.");
                return;
            }

            if (_bufferedReplicationDeltas.TryGetValue(delta.Sequence, out ReplicationDeltaBatch? duplicate))
            {
                if (StateChangePayloadSerializer.Serialize(duplicate) != StateChangePayloadSerializer.Serialize(delta))
                    FailReplication_NoLock("Conflicting duplicate replication delta.");
                return;
            }
            _bufferedReplicationDeltas.Add(delta.Sequence, delta);
            _bufferedReplicationBytes += byteCount;
            if (_replicationSynchronizationState != ClientReplicationSynchronizationState.Synchronized)
                return;
            if (_replicationApplyQueued)
                return;

            _replicationApplyQueued = true;
            attempt = _replicationAttemptGeneration;
        }

        RuntimeNetworkingHostServices.Current.EnqueueSimulation(() => ApplyNextReplicationDeltaOnSimulationThread(attempt));
    }

    private void QueueNextReplicationDelta(long attempt)
    {
        lock (_replicationSyncLock)
        {
            if (!IsReplicationAttemptCurrent_NoLock(attempt) || _replicationSynchronizationState != ClientReplicationSynchronizationState.Synchronized
                || _replicationApplyQueued || !_bufferedReplicationDeltas.ContainsKey(_expectedReplicationDeltaSequence))
                return;
            _replicationApplyQueued = true;
        }
        RuntimeNetworkingHostServices.Current.EnqueueSimulation(() => ApplyNextReplicationDeltaOnSimulationThread(attempt));
    }

    private void HandleReplicationSyncConfirmation(ReplicationSyncComplete confirmation)
    {
        long attempt;
        IRuntimeNetworkWorldContext? context;
        lock (_replicationSyncLock)
        {
            if (!MatchesReplicationAssignment_NoLock(confirmation.SessionId, confirmation.ConnectionGeneration, confirmation.CredentialEpoch)
                || confirmation.TransferId != _replicationTransferId || confirmation.SnapshotTickId != _replicationBaselineTick
                || _replicationSynchronizationState != ClientReplicationSynchronizationState.AwaitingServerConfirmation)
            {
                return;
            }

            SetReplicationState_NoLock(ClientReplicationSynchronizationState.Synchronized);
            _pendingReplicationConfirmation = null;
            attempt = _replicationAttemptGeneration;
            context = _replicationWorldContext;
        }
        RuntimeNetworkingHostServices.Current.EnqueueSimulation(() =>
        {
            if (IsReplicationAttemptCurrent(attempt))
                if (context?.WorldInstance is RuntimeWorld world)
                    world.ResumePlay();
        });
        QueueNextReplicationDelta(attempt);
    }

    internal void TickReplicationSynchronization()
    {
        ReplicationSyncComplete? retry = null;
        lock (_replicationSyncLock)
        {
            if (_replicationSynchronizationState is ClientReplicationSynchronizationState.Synchronizing or ClientReplicationSynchronizationState.ApplyingBaseline or ClientReplicationSynchronizationState.AwaitingServerConfirmation)
            {
                if (DateTime.UtcNow > _replicationDeadlineUtc || DateTime.UtcNow > _replicationAbsoluteDeadlineUtc)
                {
                    FailReplication_NoLock("Timed out waiting for a replication baseline or synchronization confirmation.");
                    return;
                }
            }

            if (_replicationSynchronizationState == ClientReplicationSynchronizationState.AwaitingServerConfirmation
                && _pendingReplicationConfirmation is not null
                && DateTime.UtcNow - _lastReplicationConfirmationUtc >= ReplicationConfirmationRetryInterval)
            {
                retry = _pendingReplicationConfirmation;
                _lastReplicationConfirmationUtc = DateTime.UtcNow;
            }
        }

        if (retry is not null)
            BroadcastStateChange(EStateChangeType.ReplicationSyncComplete, retry, compress: true);
    }

    internal void QueueReplicationPresentation(Action mutation)
    {
        long attempt;
        lock (_replicationSyncLock)
        {
            if (_replicationSynchronizationState != ClientReplicationSynchronizationState.Synchronized)
                return;
            attempt = _replicationAttemptGeneration;
        }
        RuntimeNetworkingHostServices.Current.EnqueueSimulation(() =>
        {
            if (IsReplicationAttemptCurrent(attempt) && IsGameplayReady)
                mutation();
        });
    }

    private void ApplyNextReplicationDeltaOnSimulationThread(long attempt)
    {
        ReplicationDeltaBatch? batch;
        lock (_replicationSyncLock)
        {
            if (!IsReplicationAttemptCurrent_NoLock(attempt) || !_bufferedReplicationDeltas.TryGetValue(_expectedReplicationDeltaSequence, out batch))
            {
                _replicationApplyQueued = false;
                return;
            }
        }

        lock (_replicationSyncLock)
        {
            if (!IsReplicationAttemptCurrent_NoLock(attempt))
                return;
            IReplicatedEntityWorld? world = _replicationWorldContext?.ReplicatedEntityWorld;
            if (world is null)
            {
                FailReplication_NoLock("The client has no replicated entity world.");
                return;
            }
            if (!world.ValidateDelta(batch.World, out string? error))
            {
                FailReplication_NoLock(error ?? "Replication delta validation failed.");
                return;
            }
            if (!world.ApplyDelta(batch.World, out error))
            {
                FailReplication_NoLock(error ?? "Replication delta application failed.");
                return;
            }

            _bufferedReplicationDeltas.Remove(batch.Sequence);
            _bufferedReplicationBytes -= StateChangePayloadSerializer.Serialize(batch).Length;
            _replicationBaselineTick = batch.World.TickId;
            ++_expectedReplicationDeltaSequence;
            _replicationApplyQueued = false;
            ApplyDeltaPresence_NoLock(batch);

        }
        SendReplicationAck(null, batch.Sequence);
        QueueNextReplicationDelta(attempt);
    }

    private bool MatchesReplicationAssignment_NoLock(Guid sessionId, Guid connectionGeneration, long credentialEpoch)
        => HasValidLocalAssignment && sessionId == _activeSessionId && connectionGeneration == _replicationConnectionGeneration && credentialEpoch == CredentialEpoch;

    private static bool BaselineChunksEqual(ReplicationBaselineChunk left, ReplicationBaselineChunk right)
        => StateChangePayloadSerializer.Serialize(left) == StateChangePayloadSerializer.Serialize(right);

    private static bool HasMatchingBaselineHeader(ReplicationBaselineChunk left, ReplicationBaselineChunk right)
        => left.ChunkCount == right.ChunkCount
            && left.TotalEntityCount == right.TotalEntityCount
            && left.TotalPayloadBytes == right.TotalPayloadBytes
            && left.SchemaFingerprint == right.SchemaFingerprint
            && left.WorldAsset?.IsSameAssetAs(right.WorldAsset) == true
            && CryptographicOperations.FixedTimeEquals(left.SnapshotHash, right.SnapshotHash);

    private bool IsReplicationAttemptCurrent(long attempt)
    {
        lock (_replicationSyncLock)
            return IsReplicationAttemptCurrent_NoLock(attempt);
    }

    private bool IsReplicationAttemptCurrent_NoLock(long attempt) => !_replicationDisposed && attempt == _replicationAttemptGeneration;

    private void SendReplicationAck(ReplicationBaselineChunk? chunk, uint deltaSequence)
    {
        Guid transferId;
        Guid connectionGeneration;
        long tick;
        lock (_replicationSyncLock)
        {
            transferId = chunk?.TransferId ?? _replicationTransferId;
            connectionGeneration = _replicationConnectionGeneration;
            tick = chunk?.SnapshotTickId ?? _replicationBaselineTick;
        }

        BroadcastStateChange(EStateChangeType.ReplicationTransferAck, new ReplicationTransferAck
        {
            SessionId = _activeSessionId,
            ConnectionGeneration = connectionGeneration,
            CredentialEpoch = CredentialEpoch,
            TransferId = transferId,
            ChunkIndex = chunk?.ChunkIndex ?? 0,
            SnapshotTickId = tick,
            DeltaSequence = deltaSequence,
        }, compress: true);
    }

    private void SendReplicationComplete(ReplicationBaselineChunk header)
    {
        ReplicationSyncComplete complete = new()
        {
            SessionId = header.SessionId,
            ConnectionGeneration = header.ConnectionGeneration,
            CredentialEpoch = header.CredentialEpoch,
            TransferId = header.TransferId,
            SnapshotTickId = header.SnapshotTickId,
            LastDeltaSequence = 0,
        };
        lock (_replicationSyncLock)
        {
            _pendingReplicationConfirmation = complete;
            _lastReplicationConfirmationUtc = DateTime.UtcNow;
        }
        BroadcastStateChange(EStateChangeType.ReplicationSyncComplete, complete, compress: true);
    }

    private void FailReplication(string reason)
    {
        lock (_replicationSyncLock)
            FailReplication_NoLock(reason);
    }

    private void FailReplication_NoLock(string reason)
    {
        ++_replicationAttemptGeneration;
        IRuntimeNetworkWorldContext? cleanup = _replicationWorldContext;
        ReplicationWorldOwnershipLease cleanupLease = _replicationWorldLease;
        _replicationWorldContext = null;
        _replicationWorldLease = default;
        _replicationFailure = reason;
        _baselineChunks.Clear();
        _baselineBufferedBytes = 0;
        _bufferedReplicationDeltas.Clear();
        _bufferedReplicationBytes = 0;
        _replicatedAuthorityLeases.Clear();
        _replicationApplyQueued = false;
        _pendingReplicationConfirmation = null;
        SetReplicationState_NoLock(ClientReplicationSynchronizationState.Failed);
        Debug.NetworkingWarning("[Client] Replication synchronization paused: {0}", reason);
        QueueReplicationWorldCleanup(cleanup, cleanupLease, pause: true);
        BroadcastStateChange(EStateChangeType.ReplicationResyncRequest, new ReplicationResyncRequest
        {
            SessionId = _activeSessionId,
            ConnectionGeneration = _replicationConnectionGeneration,
            CredentialEpoch = CredentialEpoch,
            TransferId = _replicationTransferId,
            ExpectedBaselineTickId = _replicationBaselineTick,
            ExpectedSequence = _expectedReplicationDeltaSequence,
            Reason = reason,
        }, compress: true);
    }

    private void SetReplicationState_NoLock(ClientReplicationSynchronizationState state)
    {
        if (_replicationSynchronizationState == state)
            return;
        _replicationSynchronizationState = state;
        ReplicationSynchronizationStateChanged?.Invoke(state);
    }

    private void QueueReplicationWorldPause_NoLock(long attempt, IRuntimeNetworkWorldContext? context)
        => RuntimeNetworkingHostServices.Current.EnqueueSimulation(() =>
        {
            if (IsReplicationAttemptCurrent(attempt))
                if (context?.WorldInstance is RuntimeWorld world)
                    world.PausePlay();
        });

    /// <summary>Invalidates queued work and cleans only this manager's bound world on the simulation thread.</summary>
    internal void DisposeReplicationSynchronization()
    {
        IRuntimeNetworkWorldContext? cleanup;
        ReplicationWorldOwnershipLease cleanupLease;
        lock (_replicationSyncLock)
        {
            _replicationDisposed = true;
            ++_replicationAttemptGeneration;
            cleanup = _replicationWorldContext;
            cleanupLease = _replicationWorldLease;
            _replicationWorldContext = null;
            _replicationWorldLease = default;
            _baselineChunks.Clear();
            _bufferedReplicationDeltas.Clear();
            _replicatedAuthorityLeases.Clear();
            _replicationApplyQueued = false;
        }
        QueueReplicationWorldCleanup(cleanup, cleanupLease, pause: true, clearAllRemotePlayers: true);
    }

    private void QueueReplicationWorldCleanup(IRuntimeNetworkWorldContext? context, ReplicationWorldOwnershipLease lease, bool pause, bool clearAllRemotePlayers = false)
    {
        if (context is null && !clearAllRemotePlayers)
            return;

        RuntimeNetworkingHostServices.Current.EnqueueSimulation(() =>
        {
            bool ownsWorld = ReplicationWorldOwnershipLeases.IsCurrent(lease, this);
            if (ownsWorld)
            {
                if (context?.WorldInstance is RuntimeWorld world && pause)
                    world.PausePlay();
                context?.ReplicatedEntityWorld?.Reset();
                ReplicationWorldOwnershipLeases.Release(lease, this);
            }

            ClearRemotePlayers(context, clearAllRemotePlayers, lease.Token);
        });
    }

    private void ApplyBaselinePresence_NoLock(ReplicatedSessionSnapshot header)
    {
        _replicatedAuthorityLeases.Clear();
        RemoveAbsentReplicationPlayers(header.Roster);
        foreach (ReplicationRosterEntry entry in header.Roster)
            if (entry.ServerPlayerIndex != _primaryAssignedServerPlayerIndex)
                BindReplicationPlayer(entry);
        foreach (NetworkAuthorityLease lease in header.Leases)
        {
            _replicatedAuthorityLeases[lease.EntityId] = lease.Clone();
            HandleAuthorityLeaseUpdate(lease);
        }
        foreach (PlayerTransformUpdate transform in header.Transforms)
            HandleRemoteTransform(transform);
        foreach (HumanoidPoseFrame pose in header.Poses)
            base.HandleStateChange(new StateChangeInfo(EStateChangeType.HumanoidPoseFrame, StateChangePayloadSerializer.Serialize(pose)), null);
    }

    private void ApplyDeltaPresence_NoLock(ReplicationDeltaBatch batch)
    {
        _replicatedAuthorityLeases.Clear();
        RemoveAbsentReplicationPlayers(batch.Roster);
        foreach (ReplicationRosterEntry entry in batch.Roster)
            if (entry.ServerPlayerIndex != _primaryAssignedServerPlayerIndex)
                BindReplicationPlayer(entry);
        foreach (NetworkAuthorityLease lease in batch.Leases)
        {
            _replicatedAuthorityLeases[lease.EntityId] = lease.Clone();
            HandleAuthorityLeaseUpdate(lease);
        }
        foreach (PlayerTransformUpdate transform in batch.Transforms)
            HandleRemoteTransform(transform);
        foreach (HumanoidPoseFrame pose in batch.Poses)
            base.HandleStateChange(new StateChangeInfo(EStateChangeType.HumanoidPoseFrame, StateChangePayloadSerializer.Serialize(pose)), null);
    }

    private void BindReplicationPlayer(ReplicationRosterEntry entry)
    {
        IRuntimeNetworkWorldContext? context = _replicationWorldContext;
        if (context is not null && GetOrCreateRemotePlayer(entry.ServerPlayerIndex, entry.DisplayName, context) is { } remote)
            context.BindRemotePawn(remote.Pawn, entry.SessionId, entry.ClientId, entry.ServerPlayerIndex);
    }

    private void RemoveAbsentReplicationPlayers(ReplicationRosterEntry[] roster)
    {
        var present = roster.Select(static entry => entry.ServerPlayerIndex).ToHashSet();
        foreach (int index in _remotePlayers.Keys.Where(index => !present.Contains(index)).ToArray())
            RemoveRemotePlayer(index);
    }
}
