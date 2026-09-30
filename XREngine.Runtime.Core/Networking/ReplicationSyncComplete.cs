using MemoryPack;

namespace XREngine.Networking;

/// <summary>Client proof that its validated baseline has been applied on the simulation thread.</summary>
[MemoryPackable]
public sealed partial class ReplicationSyncComplete
{
    public Guid SessionId { get; set; }
    public Guid ConnectionGeneration { get; set; }
    public long CredentialEpoch { get; set; }
    public Guid TransferId { get; set; }
    public long SnapshotTickId { get; set; }
    public uint LastDeltaSequence { get; set; }
}
