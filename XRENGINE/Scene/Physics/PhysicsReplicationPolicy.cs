using XREngine.Networking;

namespace XREngine.Scene.Physics;

/// <summary>
/// Describes the peer evaluating whether it may advance a replicated physics object.
/// </summary>
public readonly record struct PhysicsReplicationPeer(
    bool IsServer,
    string? ClientId = null,
    int ServerPlayerIndex = -1);

public enum PhysicsAuthorityHandoffResult
{
    Applied,
    RevertedToServer,
    EntityMismatch,
    InvalidOwner,
}

/// <summary>
/// Backend-independent authority handoff rules for rigid bodies, controllers, and joints.
/// A backend never interprets network leases itself; it only asks whether its local peer
/// is allowed to simulate the component after this policy has applied the lease metadata.
/// </summary>
public static class PhysicsReplicationPolicy
{
    public static PhysicsAuthorityHandoffResult ApplyLease(
        IPhysicsReplicationTarget target,
        NetworkAuthorityLease lease,
        double nowUtc)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(lease);

        if (lease.EntityId.IsEmpty
            || (!target.NetworkEntityId.IsEmpty && target.NetworkEntityId != lease.EntityId))
        {
            return PhysicsAuthorityHandoffResult.EntityMismatch;
        }

        if (!lease.IsActive(nowUtc))
        {
            target.ReplicationAuthority = PhysicsReplicationAuthority.ServerAuthoritative;
            target.OwnerClientId = null;
            target.OwnerServerPlayerIndex = -1;
            return PhysicsAuthorityHandoffResult.RevertedToServer;
        }

        if (lease.AuthorityMode is NetworkAuthorityMode.ClientPredicted or NetworkAuthorityMode.ServerDelegated
            && string.IsNullOrWhiteSpace(lease.OwnerClientId)
            && lease.OwnerServerPlayerIndex < 0)
        {
            return PhysicsAuthorityHandoffResult.InvalidOwner;
        }

        target.NetworkEntityId = lease.EntityId;
        target.OwnerClientId = string.IsNullOrWhiteSpace(lease.OwnerClientId) ? null : lease.OwnerClientId;
        target.OwnerServerPlayerIndex = lease.OwnerServerPlayerIndex;
        target.ReplicationAuthority = lease.AuthorityMode switch
        {
            NetworkAuthorityMode.ServerAuthoritative => PhysicsReplicationAuthority.ServerAuthoritative,
            NetworkAuthorityMode.ClientPredicted or NetworkAuthorityMode.ServerDelegated =>
                PhysicsReplicationAuthority.ClientAuthoritative,
            _ => PhysicsReplicationAuthority.ServerAuthoritative,
        };

        return PhysicsAuthorityHandoffResult.Applied;
    }

    public static bool CanSimulateLocally(
        IPhysicsReplicationTarget target,
        in PhysicsReplicationPeer localPeer)
    {
        ArgumentNullException.ThrowIfNull(target);

        return target.ReplicationAuthority switch
        {
            PhysicsReplicationAuthority.LocalSimulation => true,
            PhysicsReplicationAuthority.SharedDeterministic => true,
            PhysicsReplicationAuthority.ServerAuthoritative => localPeer.IsServer,
            PhysicsReplicationAuthority.ClientAuthoritative =>
                (!string.IsNullOrWhiteSpace(target.OwnerClientId)
                    && string.Equals(target.OwnerClientId, localPeer.ClientId, StringComparison.OrdinalIgnoreCase))
                || (target.OwnerServerPlayerIndex >= 0
                    && target.OwnerServerPlayerIndex == localPeer.ServerPlayerIndex),
            _ => false,
        };
    }
}
