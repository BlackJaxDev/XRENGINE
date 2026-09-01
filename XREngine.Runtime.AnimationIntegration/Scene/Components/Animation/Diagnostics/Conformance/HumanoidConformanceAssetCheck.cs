namespace XREngine.Components.Animation;

/// <summary>One executable expectation for a checked-in <c>.anim</c> or <c>.fbx</c> asset.</summary>
public sealed class HumanoidConformanceAssetCheck
{
    public string Id { get; set; } = string.Empty;
    public string SourceFileId { get; set; } = string.Empty;
    public HumanoidConformanceAssetCheckKind Kind { get; set; }
    public bool ExpectedToPass { get; set; }
    public HumanoidConformanceCapability ExpectedCapabilities { get; set; }
    public string Provenance { get; set; } = string.Empty;
}
