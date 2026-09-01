namespace HumanoidConformanceRunner;

/// <summary>Optional gates for a runtime imported-animation behavior probe.</summary>
internal sealed class ImportedAnimationConformanceBehaviorCheckOptions
{
    public bool RequireScalarWrite { get; set; }
    public bool RequireObjectReferenceTransition { get; set; }
    public bool RequireEvents { get; set; }
    public bool RequireSourceEncodingEvaluation { get; set; }
    public IReadOnlyList<string> ExpectedEventIds { get; set; } = [];
}
