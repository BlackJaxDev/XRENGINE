using MemoryPack;

namespace XREngine.Networking;

/// <summary>Reliable ordered update sent only after its baseline is acknowledged.</summary>
[MemoryPackable]
public sealed partial class ReplicationDeltaBatch
{
    public Guid SessionId { get; set; }
    public Guid ConnectionGeneration { get; set; }
    public long CredentialEpoch { get; set; }
    public Guid TransferId { get; set; }
    public uint Sequence { get; set; }
    public ReplicatedWorldDelta World { get; set; } = new();
    public ReplicationRosterEntry[] Roster { get; set; } = Array.Empty<ReplicationRosterEntry>();
    public NetworkAuthorityLease[] Leases { get; set; } = Array.Empty<NetworkAuthorityLease>();
    public HumanoidPoseFrame[] Poses { get; set; } = Array.Empty<HumanoidPoseFrame>();
    public PlayerTransformUpdate[] Transforms { get; set; } = Array.Empty<PlayerTransformUpdate>();
}
