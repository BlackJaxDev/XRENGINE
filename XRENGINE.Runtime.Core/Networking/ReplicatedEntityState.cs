using System.Numerics;
using MemoryPack;

namespace XREngine.Networking;

/// <summary>Canonical graph state for one replicated scene entity.</summary>
[MemoryPackable]
public sealed partial class ReplicatedEntityState
{
    public NetworkEntityId EntityId { get; set; }
    /// <summary>Immutable path in the verified loaded package, or null for a dynamically created entity.</summary>
    public string? SourcePath { get; set; }
    public NetworkEntityId? ParentId { get; set; }
    public string FactoryId { get; set; } = "scene-node-v1";
    public ushort SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    public Vector3 Translation { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public Vector3 Scale { get; set; } = Vector3.One;
    public NetworkRelevanceHint? Relevance { get; set; }
    public ReplicatedComponentState[] Components { get; set; } = Array.Empty<ReplicatedComponentState>();
}
