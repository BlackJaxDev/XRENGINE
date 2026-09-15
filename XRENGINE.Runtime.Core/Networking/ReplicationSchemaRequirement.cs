using MemoryPack;

namespace XREngine.Networking;

/// <summary>Declares a schema the receiver must explicitly support before a replication payload is applied.</summary>
[MemoryPackable]
public sealed partial class ReplicationSchemaRequirement
{
    public string SchemaId { get; set; } = string.Empty;
    public ushort SchemaVersion { get; set; }
}
