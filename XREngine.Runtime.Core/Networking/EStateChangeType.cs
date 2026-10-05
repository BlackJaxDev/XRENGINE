namespace XREngine;

/// <summary>
/// Identifies the payload carried by a realtime state-change frame. The numeric values are part
/// of the wire format; append new members and never renumber existing ones.
/// </summary>
public enum EStateChangeType : byte
{
    Invalid = 0,
    WorldChange,
    GameModeChange,
    PawnPossessionChange,
    WorldObjectCreated,
    WorldObjectDestroyed,
    SceneNodeCreated,
    SceneNodeDestroyed,
    ComponentCreated,
    ComponentDestroyed,
    PlayerJoin,
    PlayerAssignment,
    PlayerLeave,
    Heartbeat,
    PlayerInputSnapshot,
    PlayerTransformUpdate,
    RequestPlayerUpdates,
    UnrequestPlayerUpdates,
    RemoteJobRequest,
    RemoteJobResponse,
    ServerError,
    HumanoidPoseFrame,
    AuthorityLeaseUpdate,
    ClockSync,
    ReplicationSnapshot,
    ReplicationDelta,
    ReplicationBaselineChunk,
    ReplicationDeltaBatch,
    ReplicationTransferAck,
    ReplicationResyncRequest,
    ReplicationSyncComplete,
}
