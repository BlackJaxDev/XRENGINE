using MemoryPack;

namespace XREngine.Networking;

/// <summary>Opt-in serialized state for one replicated component.</summary>
[MemoryPackable]
public sealed partial class ReplicatedComponentState
{
    public Guid ComponentId { get; set; }
    public string SchemaId { get; set; } = string.Empty;
    public ushort SchemaVersion { get; set; }
    public bool Active { get; set; } = true;
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public NetworkEntityId[] EntityReferences { get; set; } = Array.Empty<NetworkEntityId>();
    public string[] AssetReferences { get; set; } = Array.Empty<string>();
}
