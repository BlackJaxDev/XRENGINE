using MemoryPack;

namespace XREngine.Animation.Importers;

/// <summary>
/// Source YAML retained when the current native evaluator cannot execute a
/// behaviorally relevant section yet.
/// </summary>
[MemoryPackable]
public sealed partial class ImportedAnimationPreservedPayload
{
    public EImportedAnimationDataDomain Domain { get; set; }
    public string SourceLocation { get; set; } = string.Empty;
    public string SerializedYaml { get; set; } = string.Empty;
}
