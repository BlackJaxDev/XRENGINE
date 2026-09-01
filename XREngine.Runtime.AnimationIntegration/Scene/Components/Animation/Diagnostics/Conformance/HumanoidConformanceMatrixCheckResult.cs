namespace XREngine.Components.Animation;

/// <summary>Observed outcome of a runtime playback matrix case.</summary>
public sealed class HumanoidConformanceMatrixCheckResult
{
    public string MatrixCaseId { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public HumanoidConformanceCapability ObservedCapabilities { get; set; }
    public string Diagnostic { get; set; } = string.Empty;
}
