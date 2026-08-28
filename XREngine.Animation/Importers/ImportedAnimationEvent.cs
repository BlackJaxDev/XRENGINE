using MemoryPack;

namespace XREngine.Animation.Importers;

/// <summary>
/// An allowlisted native event converted from imported animation data.
/// <see cref="EventId"/> is a native identifier and is never interpreted as a component method.
/// </summary>
[MemoryPackable]
public sealed partial class ImportedAnimationEvent
{
    public float Time { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string StringParameter { get; set; } = string.Empty;
    public float FloatParameter { get; set; }
    public int IntParameter { get; set; }
    public SourceAssetReference ObjectReferenceParameter { get; set; }
    public EImportedAnimationEventMessageOptions MessageOptions { get; set; }
    public int SourceOrder { get; set; }
}
