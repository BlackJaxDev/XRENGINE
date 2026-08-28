using MemoryPack;

namespace XREngine.Animation.Importers;

/// <summary>
/// Aggregate import result for one Unity animation data domain.
/// </summary>
[MemoryPackable]
public sealed partial class ImportedAnimationDomainCapability
{
    public EImportedAnimationDataDomain Domain { get; set; }
    public EImportedAnimationCapabilityState State { get; set; }
    public int SourceItemCount { get; set; }
    public int AppliedItemCount { get; set; }
    public int DiscardedItemCount { get; set; }
    public int PreservedItemCount { get; set; }
    public string[] Diagnostics { get; set; } = [];
}
