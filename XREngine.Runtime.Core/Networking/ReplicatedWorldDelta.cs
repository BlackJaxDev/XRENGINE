using MemoryPack;

namespace XREngine.Networking;

/// <summary>Ordered difference from a previously acknowledged canonical snapshot.</summary>
[MemoryPackable]
public sealed partial class ReplicatedWorldDelta
{
    public Guid SessionId { get; set; }
    public long TickId { get; set; }
    public long BaseTickId { get; set; }
    public ReplicatedEntityState[] Upserts { get; set; } = Array.Empty<ReplicatedEntityState>();
    public NetworkEntityId[] DestroyedEntityIds { get; set; } = Array.Empty<NetworkEntityId>();
}
