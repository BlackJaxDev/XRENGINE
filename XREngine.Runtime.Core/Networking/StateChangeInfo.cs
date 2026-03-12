using MemoryPack;

namespace XREngine;

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
}

[MemoryPackable]
public sealed partial class StateChangeInfo
{
    public StateChangeInfo()
    {
    }

    [MemoryPackConstructor]
    public StateChangeInfo(EStateChangeType type, string data)
    {
        Type = type;
        Data = data;
    }

    public EStateChangeType Type { get; set; }
    public string Data { get; set; } = string.Empty;
}
