using System;
using System.Collections.Generic;
using System.Numerics;
using MemoryPack;

namespace XREngine.Networking;

public enum NetworkAuthorityMode : byte
{
    None = 0,
    ServerAuthoritative = 1,
    ClientPredicted = 2,
    ServerDelegated = 3,
}

public enum NetworkAuthorityRevocationReason : byte
{
    None = 0,
    LeaseExpired = 1,
    OwnerDisconnected = 2,
    OwnerLeft = 3,
    InvalidOwner = 4,
    SessionEnded = 5,
    OperatorRevoked = 6,
    Superseded = 7,
}

public enum NetworkReplicationChannel : byte
{
    GenericSnapshot = 0,
    GenericDelta = 1,
    Transform = 2,
    PlayerInput = 3,
    HumanoidPose = 4,
}

[MemoryPackable]
public readonly partial record struct NetworkEntityId(Guid Value)
{
    public static NetworkEntityId Empty { get; } = new(Guid.Empty);

    [MemoryPackIgnore]
    public bool IsEmpty => Value == Guid.Empty;

    public static NetworkEntityId FromGuid(Guid value)
        => value == Guid.Empty ? Empty : new NetworkEntityId(value);

    public override string ToString()
        => Value == Guid.Empty ? string.Empty : Value.ToString("N");
}

[MemoryPackable]
public sealed partial class NetworkAuthorityLease
{
    public NetworkEntityId EntityId { get; set; }
    public Guid SessionId { get; set; }
    public string OwnerClientId { get; set; } = string.Empty;
    public int OwnerServerPlayerIndex { get; set; } = -1;
    public NetworkAuthorityMode AuthorityMode { get; set; } = NetworkAuthorityMode.None;
    public double AuthorityLeaseExpiryUtc { get; set; }
    public NetworkAuthorityRevocationReason RevocationReason { get; set; } = NetworkAuthorityRevocationReason.None;
    public string? RevocationDetail { get; set; }

    [MemoryPackIgnore]
    public bool IsRevoked => RevocationReason != NetworkAuthorityRevocationReason.None;

    public bool IsActive(double nowUtc)
        => !EntityId.IsEmpty
            && AuthorityMode != NetworkAuthorityMode.None
            && !IsRevoked
            && (AuthorityLeaseExpiryUtc <= 0.0d || AuthorityLeaseExpiryUtc > nowUtc);

    public NetworkAuthorityLease Clone()
        => new()
        {
            EntityId = EntityId,
            SessionId = SessionId,
            OwnerClientId = OwnerClientId,
            OwnerServerPlayerIndex = OwnerServerPlayerIndex,
            AuthorityMode = AuthorityMode,
            AuthorityLeaseExpiryUtc = AuthorityLeaseExpiryUtc,
            RevocationReason = RevocationReason,
            RevocationDetail = RevocationDetail
        };
}

[MemoryPackable]
public sealed partial class NetworkSnapshotEnvelope
{
    public Guid SessionId { get; set; }
    public NetworkReplicationChannel Channel { get; set; } = NetworkReplicationChannel.GenericSnapshot;
    public long ServerTickId { get; set; }
    public long BaselineTickId { get; set; }
    public uint SnapshotSequence { get; set; }
    public double ServerTimestampUtc { get; set; }
    public NetworkEntityId[] EntityIds { get; set; } = Array.Empty<NetworkEntityId>();
    public byte[] Payload { get; set; } = Array.Empty<byte>();
}

[MemoryPackable]
public sealed partial class NetworkDeltaEnvelope
{
    public Guid SessionId { get; set; }
    public NetworkReplicationChannel Channel { get; set; } = NetworkReplicationChannel.GenericDelta;
    public long ServerTickId { get; set; }
    public long BaselineTickId { get; set; }
    public uint DeltaSequence { get; set; }
    public double ServerTimestampUtc { get; set; }
    public NetworkEntityId[] EntityIds { get; set; } = Array.Empty<NetworkEntityId>();
    public byte[] Payload { get; set; } = Array.Empty<byte>();
}

[MemoryPackable]
public sealed partial class ClockSyncMessage
{
    public Guid SessionId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public int ServerPlayerIndex { get; set; } = -1;
    public double ClientSendTimestampUtc { get; set; }
    public double ServerReceiveTimestampUtc { get; set; }
    public double ServerSendTimestampUtc { get; set; }
    public long ServerTickId { get; set; }
}

[MemoryPackable]
public sealed partial class NetworkRelevanceHint
{
    public NetworkEntityId EntityId { get; set; }
    public Vector3 Center { get; set; }
    public float Radius { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();

    public bool Contains(Vector3 point)
        => Radius <= 0.0f || Vector3.DistanceSquared(Center, point) <= Radius * Radius;
}

[MemoryPackable]
public sealed partial class NetworkReplicationBudgetState
{
    public string ClientId { get; set; } = string.Empty;
    public int ServerPlayerIndex { get; set; } = -1;
    public int BytesPerSecond { get; set; }
    public int BytesAvailable { get; set; }
    public double UpdatedUtc { get; set; }
}

public sealed class NetworkBandwidthBudget
{
    private readonly int _bytesPerSecond;
    private double _availableBytes;
    private double _lastUpdateUtc;

    public NetworkBandwidthBudget(int bytesPerSecond, double nowUtc)
    {
        _bytesPerSecond = Math.Max(0, bytesPerSecond);
        _availableBytes = _bytesPerSecond;
        _lastUpdateUtc = nowUtc;
    }

    public int BytesPerSecond => _bytesPerSecond;

    public int BytesAvailable(double nowUtc)
    {
        Refill(nowUtc);
        return (int)Math.Floor(_availableBytes);
    }

    public bool TryConsume(int byteCount, double nowUtc)
    {
        if (_bytesPerSecond <= 0)
            return true;

        Refill(nowUtc);
        int clampedByteCount = Math.Max(0, byteCount);
        if (_availableBytes < clampedByteCount)
            return false;

        _availableBytes -= clampedByteCount;
        return true;
    }

    public NetworkReplicationBudgetState CreateState(string clientId, int serverPlayerIndex, double nowUtc)
        => new()
        {
            ClientId = clientId,
            ServerPlayerIndex = serverPlayerIndex,
            BytesPerSecond = _bytesPerSecond,
            BytesAvailable = BytesAvailable(nowUtc),
            UpdatedUtc = nowUtc
        };

    private void Refill(double nowUtc)
    {
        if (_bytesPerSecond <= 0)
            return;

        double elapsed = Math.Max(0.0d, nowUtc - _lastUpdateUtc);
        _availableBytes = Math.Min(_bytesPerSecond, _availableBytes + elapsed * _bytesPerSecond);
        _lastUpdateUtc = nowUtc;
    }
}

public sealed class RealtimeReplicationCoordinator
{
    private readonly Dictionary<NetworkEntityId, NetworkAuthorityLease> _leases = [];
    private readonly Dictionary<int, Queue<PlayerInputSnapshot>> _inputBuffers = [];
    private readonly TimeSpan _inputBufferWindow;
    private long _serverTickId;
    private uint _snapshotSequence;
    private uint _deltaSequence;

    public RealtimeReplicationCoordinator(TimeSpan? inputBufferWindow = null)
    {
        _inputBufferWindow = inputBufferWindow ?? MultiplayerRuntimePolicy.InputBufferWindow;
    }

    public long CurrentServerTickId => _serverTickId;

    public long AdvanceServerTick()
        => ++_serverTickId;

    public NetworkAuthorityLease GrantLease(
        NetworkEntityId entityId,
        Guid sessionId,
        string ownerClientId,
        int ownerServerPlayerIndex,
        double nowUtc,
        TimeSpan? duration = null,
        NetworkAuthorityMode authorityMode = NetworkAuthorityMode.ClientPredicted)
    {
        TimeSpan leaseDuration = duration ?? MultiplayerRuntimePolicy.AuthorityLeaseDuration;
        NetworkAuthorityLease lease = new()
        {
            EntityId = entityId,
            SessionId = sessionId,
            OwnerClientId = ownerClientId,
            OwnerServerPlayerIndex = ownerServerPlayerIndex,
            AuthorityMode = authorityMode,
            AuthorityLeaseExpiryUtc = nowUtc + Math.Max(0.0d, leaseDuration.TotalSeconds)
        };

        _leases[entityId] = lease;
        return lease.Clone();
    }

    public NetworkAuthorityLease? GetLease(NetworkEntityId entityId)
        => _leases.TryGetValue(entityId, out NetworkAuthorityLease? lease) ? lease.Clone() : null;

    public NetworkAuthorityLease? RevokeLease(
        NetworkEntityId entityId,
        NetworkAuthorityRevocationReason reason,
        string? detail = null)
    {
        if (!_leases.TryGetValue(entityId, out NetworkAuthorityLease? lease))
            return null;

        lease.AuthorityMode = NetworkAuthorityMode.None;
        lease.AuthorityLeaseExpiryUtc = 0.0d;
        lease.RevocationReason = reason;
        lease.RevocationDetail = detail;
        return lease.Clone();
    }

    public bool TryValidateOwner(
        NetworkEntityId entityId,
        string clientId,
        int serverPlayerIndex,
        Guid sessionId,
        double nowUtc,
        out NetworkAuthorityLease? lease,
        out NetworkAuthorityRevocationReason failureReason)
    {
        lease = null;
        failureReason = NetworkAuthorityRevocationReason.None;

        if (entityId.IsEmpty || !_leases.TryGetValue(entityId, out NetworkAuthorityLease? storedLease))
        {
            failureReason = NetworkAuthorityRevocationReason.InvalidOwner;
            return false;
        }

        lease = storedLease.Clone();
        if (!storedLease.IsActive(nowUtc))
        {
            failureReason = storedLease.IsRevoked
                ? storedLease.RevocationReason
                : NetworkAuthorityRevocationReason.LeaseExpired;
            return false;
        }

        if (storedLease.SessionId != sessionId
            || storedLease.OwnerServerPlayerIndex != serverPlayerIndex
            || !string.Equals(storedLease.OwnerClientId, clientId, StringComparison.OrdinalIgnoreCase))
        {
            failureReason = NetworkAuthorityRevocationReason.InvalidOwner;
            return false;
        }

        return true;
    }

    public int BufferInput(PlayerInputSnapshot snapshot, double serverTimeUtc)
    {
        if (!_inputBuffers.TryGetValue(snapshot.ServerPlayerIndex, out Queue<PlayerInputSnapshot>? buffer))
        {
            buffer = new Queue<PlayerInputSnapshot>();
            _inputBuffers[snapshot.ServerPlayerIndex] = buffer;
        }

        buffer.Enqueue(snapshot);
        double oldestAllowed = serverTimeUtc - _inputBufferWindow.TotalSeconds;
        while (buffer.TryPeek(out PlayerInputSnapshot? oldest)
            && oldest.ClientSendTimestampUtc > 0.0d
            && oldest.ClientSendTimestampUtc < oldestAllowed)
        {
            buffer.Dequeue();
        }

        while (buffer.Count > MultiplayerRuntimePolicy.MaxBufferedInputsPerPlayer)
            buffer.Dequeue();

        return buffer.Count;
    }

    public uint LastBufferedInputSequence(int serverPlayerIndex)
    {
        if (!_inputBuffers.TryGetValue(serverPlayerIndex, out Queue<PlayerInputSnapshot>? buffer) || buffer.Count == 0)
            return 0;

        uint last = 0;
        foreach (PlayerInputSnapshot input in buffer)
            last = input.InputSequence;
        return last;
    }

    public PlayerTransformUpdate StampAuthoritativeTransform(PlayerTransformUpdate transform, double serverTimeUtc)
    {
        transform.ServerTickId = CurrentServerTickId == 0 ? AdvanceServerTick() : CurrentServerTickId;
        transform.ServerTimestampUtc = serverTimeUtc;
        transform.LastProcessedInputSequence = LastBufferedInputSequence(transform.ServerPlayerIndex);
        transform.AuthorityMode = NetworkAuthorityMode.ServerAuthoritative;
        transform.IsServerCorrection = true;
        return transform;
    }

    public NetworkSnapshotEnvelope CreateSnapshot(
        Guid sessionId,
        NetworkReplicationChannel channel,
        IEnumerable<NetworkEntityId> entityIds,
        byte[] payload,
        double serverTimeUtc)
        => new()
        {
            SessionId = sessionId,
            Channel = channel,
            ServerTickId = CurrentServerTickId == 0 ? AdvanceServerTick() : CurrentServerTickId,
            BaselineTickId = 0,
            SnapshotSequence = ++_snapshotSequence,
            ServerTimestampUtc = serverTimeUtc,
            EntityIds = [.. entityIds],
            Payload = payload
        };

    public NetworkDeltaEnvelope CreateDelta(
        Guid sessionId,
        NetworkReplicationChannel channel,
        long baselineTickId,
        IEnumerable<NetworkEntityId> entityIds,
        byte[] payload,
        double serverTimeUtc)
        => new()
        {
            SessionId = sessionId,
            Channel = channel,
            ServerTickId = CurrentServerTickId == 0 ? AdvanceServerTick() : CurrentServerTickId,
            BaselineTickId = baselineTickId,
            DeltaSequence = ++_deltaSequence,
            ServerTimestampUtc = serverTimeUtc,
            EntityIds = [.. entityIds],
            Payload = payload
        };
}
