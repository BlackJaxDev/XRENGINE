namespace XREngine.Components.Animation;

/// <summary>Result of loading and validating a conformance manifest.</summary>
public sealed class HumanoidConformanceValidationResult
{
    public string ManifestPath { get; set; } = string.Empty;
    public HumanoidConformanceManifest? Manifest { get; set; }
    public List<HumanoidConformanceValidationIssue> Issues { get; set; } = [];
    public bool IsValid => Manifest is not null && Issues.Count == 0;
}
