using MemoryPack;

namespace XREngine.Networking;

/// <summary>Acknowledges one baseline chunk after it was buffered without mutation.</summary>
[MemoryPackable]
public sealed partial class ReplicationTransferAck
{
    public Guid SessionId { get; set; }
    public Guid ConnectionGeneration { get; set; }
    public long CredentialEpoch { get; set; }
    public Guid TransferId { get; set; }
    public uint ChunkIndex { get; set; }
    public long SnapshotTickId { get; set; }
    /// <summary>Non-zero when this acknowledges an applied ordered delta rather than a baseline chunk.</summary>
    public uint DeltaSequence { get; set; }
}
