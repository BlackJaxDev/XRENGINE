using MemoryPack;

namespace XREngine.Networking;

/// <summary>One hashed and byte-chunked simulation cut including world graph and stationary player state.</summary>
[MemoryPackable]
public sealed partial class ReplicatedSessionSnapshot
{
    public ReplicatedWorldSnapshot World { get; set; } = new();
    public ReplicationRosterEntry[] Roster { get; set; } = [];
    public NetworkAuthorityLease[] Leases { get; set; } = [];
    public HumanoidPoseFrame[] Poses { get; set; } = [];
    public PlayerTransformUpdate[] Transforms { get; set; } = [];
}
