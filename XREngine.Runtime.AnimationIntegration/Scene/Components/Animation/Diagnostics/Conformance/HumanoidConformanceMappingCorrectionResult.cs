namespace XREngine.Components.Animation;

/// <summary>Structured outcome of validating and applying a mapping correction.</summary>
public sealed class HumanoidConformanceMappingCorrectionResult
{
    public string SidecarPath { get; set; } = string.Empty;
    public HumanoidConformanceMappingCorrection? Correction { get; set; }
    public string MappingSignature { get; set; } = string.Empty;
    public string AppliedAvatarDefinitionSignature { get; set; } = string.Empty;
    public bool Applied { get; set; }
    public List<HumanoidConformanceMappingCorrectionIssue> Issues { get; set; } = [];
    public bool IsValid => Correction is not null && Issues.Count == 0;
}
