using XREngine.Animation.Importers;
using XREngine.Components.Animation;

namespace HumanoidConformanceRunner;

/// <summary>Readback from evaluating one imported animation through the public runtime path.</summary>
internal sealed class ImportedAnimationConformanceBehaviorCheckResult
{
    public bool Passed => Failures.Count == 0;
    public int ContractScalarBindingCount { get; set; }
    public int ContractObjectReferenceBindingCount { get; set; }
    public int ImportedEventCount { get; set; }
    public int ScalarWriteCount { get; set; }
    public int ObjectReferenceWriteCount { get; set; }
    public bool ObservedNonNullObjectReference { get; set; }
    public bool ObservedNullObjectReference { get; set; }
    public bool ObservedNonNullThenNullObjectReference { get; set; }
    public bool ObservedForwardEvent { get; set; }
    public bool ObservedReverseEvent { get; set; }
    public bool ForwardEventPayloadsMatch { get; set; }
    public bool ReverseEventPayloadsMatch { get; set; }
    public bool ObservedTransformChange { get; set; }
    public bool ObservedSourceEncodingEvaluation { get; set; }
    public List<ImportedAnimationConformanceEventObservation> Events { get; set; } = [];
    public List<float> ScalarWrites { get; set; } = [];
    public List<SourceAssetReference> ObjectReferenceWrites { get; set; } = [];
    public List<string> Failures { get; set; } = [];
}
