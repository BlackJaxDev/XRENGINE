using MemoryPack;

namespace XREngine.Networking;

/// <summary>Immutable canonical world state captured on the simulation thread for one server tick.</summary>
[MemoryPackable]
public sealed partial class ReplicatedWorldSnapshot
{
    public Guid SessionId { get; set; }
    public long TickId { get; set; }
    public ReplicatedEntityState[] Entities { get; set; } = Array.Empty<ReplicatedEntityState>();
    public string[] RequiredScenes { get; set; } = Array.Empty<string>();
    public Guid[] RequiredSceneIds { get; set; } = Array.Empty<Guid>();
    public ReplicationSchemaRequirement[] RequiredSchemas { get; set; } = Array.Empty<ReplicationSchemaRequirement>();
}
