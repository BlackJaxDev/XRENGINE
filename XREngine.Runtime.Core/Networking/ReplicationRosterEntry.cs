using MemoryPack;

namespace XREngine.Networking;

/// <summary>Credential-free presence information included in baseline and ordered delta transfers.</summary>
[MemoryPackable]
public sealed partial class ReplicationRosterEntry
{
    public Guid SessionId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public int ServerPlayerIndex { get; set; } = -1;
    public NetworkEntityId PlayerEntityId { get; set; }
    public Guid TransformId { get; set; }
    public string? DisplayName { get; set; }
}
