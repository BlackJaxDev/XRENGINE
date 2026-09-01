using XREngine.Animation.Importers;

namespace XREngine.Components.Animation;

/// <summary>Immutable readback of one typed imported event observed by the conformance probe.</summary>
public sealed class ImportedAnimationConformanceEventObservation
{
    public string EventId { get; init; } = string.Empty;
    public float EventTime { get; init; }
    public string StringParameter { get; init; } = string.Empty;
    public float FloatParameter { get; init; }
    public int IntParameter { get; init; }
    public int SourceOrder { get; init; }
    public SourceAssetReference ObjectReferenceParameter { get; init; }
    public EImportedAnimationEventMessageOptions MessageOptions { get; init; }
    public long LoopCycle { get; init; }
    public bool Reverse { get; init; }
    public ulong MotionOccurrenceId { get; init; }
    public string StateName { get; init; } = string.Empty;
    public float BlendWeight { get; init; }
}
