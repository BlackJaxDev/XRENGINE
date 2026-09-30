using XREngine.Networking;

namespace XREngine;

public partial class ServerNetworkingManager
{
    private readonly Dictionary<NetworkEntityId, Guid> _serverAuthorizedInteractables = [];

    /// <summary>Registers a replicated entity that game code permits to receive an authority lease.</summary>
    public bool RegisterServerAuthorizedInteractable(NetworkEntityId entityId, Guid sessionId)
    {
        if (entityId.IsEmpty || sessionId == Guid.Empty)
            return false;
        lock (_playerLock)
            return _serverAuthorizedInteractables.TryAdd(entityId, sessionId);
    }

    public void UnregisterServerAuthorizedInteractable(NetworkEntityId entityId, NetworkAuthorityRevocationReason reason = NetworkAuthorityRevocationReason.SessionEnded)
    {
        NetworkAuthorityLease? revoked;
        lock (_playerLock)
        {
            _serverAuthorizedInteractables.Remove(entityId);
            revoked = _replication.RevokeLease(entityId, reason, "Interactable unregistered.");
        }
        if (revoked is not null)
            BroadcastAuthorityLeaseUpdate(revoked);
    }

    public NetworkAuthorityLease? GetServerAuthorizedInteractableLease(NetworkEntityId entityId)
    {
        lock (_playerLock)
            return _serverAuthorizedInteractables.ContainsKey(entityId) ? _replication.GetLease(entityId) : null;
    }

    /// <summary>
    /// Server/game-authorized acquisition. A client never names an arbitrary
    /// world object on the realtime wire; game code first resolves and registers
    /// its replicated entity, then invokes this method on simulation.
    /// </summary>
    public bool TryGrantServerAuthorizedInteractable(NetworkEntityId entityId, int requesterServerPlayerIndex, out NetworkAuthorityLease? lease)
    {
        lease = null;
        lock (_playerLock)
        {
            if (!_serverAuthorizedInteractables.TryGetValue(entityId, out Guid sessionId)
                || !_playersByIndex.TryGetValue(requesterServerPlayerIndex, out NetworkPlayerConnection? requester)
                || requester.SessionId != sessionId)
            {
                return false;
            }

            if (!_replication.TryGrantLease(entityId, sessionId, requester.ClientId, requester.ServerPlayerIndex, GetUtcSeconds(), out lease, authorityMode: NetworkAuthorityMode.ServerDelegated))
                return false;
        }
        BroadcastAuthorityLeaseUpdate(lease!);
        return true;
    }

    /// <summary>Transfers only after the server has explicitly resolved the conflict and revoked the prior owner.</summary>
    public bool TryTransferServerAuthorizedInteractable(NetworkEntityId entityId, int recipientServerPlayerIndex, out NetworkAuthorityLease? granted)
    {
        granted = null;
        NetworkAuthorityLease? revoked;
        lock (_playerLock)
        {
            if (!_serverAuthorizedInteractables.TryGetValue(entityId, out Guid sessionId)
                || !_playersByIndex.TryGetValue(recipientServerPlayerIndex, out NetworkPlayerConnection? recipient)
                || recipient.SessionId != sessionId)
            {
                return false;
            }

            revoked = _replication.RevokeLease(entityId, NetworkAuthorityRevocationReason.Superseded, "Server-authorized transfer.");
            if (!_replication.TryGrantLease(entityId, sessionId, recipient.ClientId, recipient.ServerPlayerIndex, GetUtcSeconds(), out granted, authorityMode: NetworkAuthorityMode.ServerDelegated))
                return false;
        }
        if (revoked is not null)
            BroadcastAuthorityLeaseUpdate(revoked);
        BroadcastAuthorityLeaseUpdate(granted!);
        return true;
    }
}
