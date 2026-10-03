using XREngine.Data;

namespace XREngine.Networking;

/// <summary>Statically roots networking DTO identities for published type resolution.</summary>
public static class NetworkingAotContractRegistry
{
    private static readonly (Type Type, string Id)[] Contracts =
    [
        (typeof(RemoteJobRequest), "xre.network.remote-job-request"),
        (typeof(RemoteJobResponse), "xre.network.remote-job-response"),
        (typeof(PlayerJoinRequest), "xre.network.player-join-request"),
        (typeof(PlayerAssignment), "xre.network.player-assignment"),
        (typeof(PlayerInputSnapshot), "xre.network.player-input-snapshot"),
        (typeof(WorldSyncDescriptor), "xre.network.world-sync-descriptor"),
        (typeof(PlayerTransformUpdate), "xre.network.player-transform-update"),
        (typeof(PlayerLeaveNotice), "xre.network.player-leave-notice"),
        (typeof(PlayerHeartbeat), "xre.network.player-heartbeat"),
        (typeof(ServerErrorMessage), "xre.network.server-error-message"),
        (typeof(HumanoidPoseFrame), "xre.network.humanoid-pose-frame"),
        (typeof(NetworkEntityId), "xre.network.network-entity-id"),
        (typeof(NetworkAuthorityLease), "xre.network.network-authority-lease"),
        (typeof(NetworkSnapshotEnvelope), "xre.network.network-snapshot-envelope"),
        (typeof(NetworkDeltaEnvelope), "xre.network.network-delta-envelope"),
        (typeof(ClockSyncMessage), "xre.network.clock-sync-message"),
        (typeof(NetworkRelevanceHint), "xre.network.network-relevance-hint"),
        (typeof(NetworkReplicationBudgetState), "xre.network.network-replication-budget-state"),
        (typeof(ReplicationSchemaRequirement), "xre.network.replication-schema-requirement"),
        (typeof(ReplicatedComponentState), "xre.network.replicated-component-state"),
        (typeof(ReplicatedEntityState), "xre.network.replicated-entity-state"),
        (typeof(ReplicatedWorldSnapshot), "xre.network.replicated-world-snapshot"),
        (typeof(ReplicatedSessionSnapshot), "xre.network.replicated-session-snapshot"),
        (typeof(ReplicatedWorldDelta), "xre.network.replicated-world-delta"),
        (typeof(ReplicationBaselineChunk), "xre.network.replication-baseline-chunk"),
        (typeof(ReplicationBaselinePayload), "xre.network.replication-baseline-payload"),
        (typeof(ReplicationRosterEntry), "xre.network.replication-roster-entry"),
        (typeof(ReplicationDeltaBatch), "xre.network.replication-delta-batch"),
        (typeof(ReplicationTransferAck), "xre.network.replication-transfer-ack"),
        (typeof(ReplicationResyncRequest), "xre.network.replication-resync-request"),
        (typeof(ReplicationSyncComplete), "xre.network.replication-sync-complete"),
        (typeof(WorldAssetIdentity), "xre.network.world-asset-identity"),
        (typeof(RealtimeEndpointDescriptor), "xre.network.realtime-endpoint-descriptor"),
    ];

    /// <summary>Types included in the published networking metadata table.</summary>
    public static Type[] ContractTypes { get; } = [.. Contracts.Select(static contract => contract.Type)];

    /// <summary>Installs the static DTO type contracts for the lifetime of runtime asset services.</summary>
    public static IDisposable Install()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            foreach ((Type type, string id) in Contracts)
                leases.Add(RuntimeTypeContractRegistry.Register(type, id, schemaVersion: 1));
        });
}
