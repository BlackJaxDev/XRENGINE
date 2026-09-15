using MemoryPack;

namespace XREngine.Networking;

/// <summary>Binary fragment of one immutable, hashed <see cref="ReplicatedWorldSnapshot"/> baseline.</summary>
[MemoryPackable]
public sealed partial class ReplicationBaselinePayload
{
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
}
