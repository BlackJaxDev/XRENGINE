using MemoryPack;

namespace XREngine.Networking;

/// <summary>One ordered, acknowledged portion of a baseline transfer.</summary>
[MemoryPackable]
public sealed partial class ReplicationBaselineChunk
{
    public Guid SessionId { get; set; }
    /// <summary>Per-admission connection nonce, distinct from the worker generation.</summary>
    public Guid ConnectionGeneration { get; set; }
    public long CredentialEpoch { get; set; }
    public Guid TransferId { get; set; }
    public long SnapshotTickId { get; set; }
    public uint ChunkIndex { get; set; }
    public uint ChunkCount { get; set; }
    public int TotalEntityCount { get; set; }
    public int TotalPayloadBytes { get; set; }
    public byte[] SnapshotHash { get; set; } = Array.Empty<byte>();
    public string SchemaFingerprint { get; set; } = string.Empty;
    public WorldAssetIdentity? WorldAsset { get; set; }
    public ReplicationBaselinePayload Payload { get; set; } = new();
}
