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
        typeof(WorldAssetIdentity),
        typeof(RealtimeEndpointDescriptor),
    ];
}
