namespace XREngine.Components.Animation;

/// <summary>A validation or application problem found in a persisted mapping correction.</summary>
public sealed class HumanoidConformanceMappingCorrectionIssue
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
