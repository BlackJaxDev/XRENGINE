namespace XREngine.Components.Animation;

/// <summary>One animation source in the conformance corpus.</summary>
public sealed class HumanoidConformanceClip
{
    public string Id { get; set; } = string.Empty;
    public string SourceFileId { get; set; } = string.Empty;
    public string ClipSignature { get; set; } = string.Empty;
    public string ImportSettingsHash { get; set; } = string.Empty;
    public bool IsIntegrationOnly { get; set; }
    public HumanoidConformanceCapability ExpectedCapabilities { get; set; }
    public List<string> CompatibleAvatarIds { get; set; } = [];
    public HumanoidConformanceCoordinateSpaces CoordinateSpaces { get; set; } = new();
}
