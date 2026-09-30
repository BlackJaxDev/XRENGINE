using MemoryPack;

namespace XREngine.Networking;

/// <summary>Requests a fresh baseline after an ordered transfer cannot be validated or recovered.</summary>
[MemoryPackable]
public sealed partial class ReplicationResyncRequest
{
    public Guid SessionId { get; set; }
    public Guid ConnectionGeneration { get; set; }
    public long CredentialEpoch { get; set; }
    public Guid TransferId { get; set; }
    public long ExpectedBaselineTickId { get; set; }
    public uint ExpectedSequence { get; set; }
    public string Reason { get; set; } = string.Empty;
}
