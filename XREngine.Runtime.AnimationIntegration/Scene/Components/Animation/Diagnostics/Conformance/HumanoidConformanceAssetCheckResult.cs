namespace XREngine.Components.Animation;

/// <summary>Observed outcome of one executable corpus asset check.</summary>
public sealed class HumanoidConformanceAssetCheckResult
{
    public string AssetCheckId { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public HumanoidConformanceCapability ObservedCapabilities { get; set; }
    public string Diagnostic { get; set; } = string.Empty;
}
