using System;

namespace XREngine.Networking;

public static class NetworkingAotContractRegistry
{
    public static Type[] ContractTypes { get; } =
    [
        typeof(StateChangeInfo),
        typeof(PlayerJoinRequest),
        typeof(PlayerAssignment),
        typeof(PlayerInputSnapshot),
        typeof(WorldSyncDescriptor),
        typeof(PlayerTransformUpdate),
        typeof(PlayerLeaveNotice),
        typeof(PlayerHeartbeat),
        typeof(ServerErrorMessage),
        typeof(HumanoidPoseFrame),
        typeof(NetworkEntityId),
        typeof(NetworkAuthorityLease),
        typeof(NetworkSnapshotEnvelope),
        typeof(NetworkDeltaEnvelope),
        typeof(ClockSyncMessage),
        typeof(NetworkRelevanceHint),
        typeof(NetworkReplicationBudgetState),
        typeof(ReplicationSchemaRequirement),
        typeof(ReplicatedComponentState),
        typeof(ReplicatedEntityState),
        typeof(ReplicatedWorldSnapshot),
        typeof(ReplicatedSessionSnapshot),
        typeof(ReplicatedWorldDelta),
        typeof(ReplicationBaselineChunk),
        typeof(ReplicationBaselinePayload),
        typeof(ReplicationRosterEntry),
        typeof(ReplicationDeltaBatch),
        typeof(ReplicationTransferAck),
        typeof(ReplicationResyncRequest),
        typeof(ReplicationSyncComplete),
        typeof(WorldAssetIdentity),
        typeof(RealtimeEndpointDescriptor),
    ];
}
