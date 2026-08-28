using MemoryPack;

namespace XREngine.Animation.Importers;

/// <summary>
/// Bounded provenance for source data that the native evaluator cannot execute.
/// </summary>
[MemoryPackable]
public sealed partial class ImportedAnimationPreservedPayload
{
    public EImportedAnimationDataDomain Domain { get; set; }
    public string SourceLocation { get; set; } = string.Empty;
    public int SerializedPayloadByteCount { get; set; }
    public string SerializedPayloadSha256 { get; set; } = string.Empty;
    public bool ContentOmitted { get; set; } = true;
}
