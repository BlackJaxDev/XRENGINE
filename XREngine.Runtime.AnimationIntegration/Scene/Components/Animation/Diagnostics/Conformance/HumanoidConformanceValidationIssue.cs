namespace XREngine.Components.Animation;

/// <summary>A deterministic manifest validation failure or warning.</summary>
public sealed class HumanoidConformanceValidationIssue
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
