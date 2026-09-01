namespace XREngine.Components.Animation;

/// <summary>One redistributable humanoid avatar in the conformance corpus.</summary>
public sealed class HumanoidConformanceAvatar
{
    public string Id { get; set; } = string.Empty;
    public string SourceFileId { get; set; } = string.Empty;
    public string AvatarDefinitionSignature { get; set; } = string.Empty;
    public string ImportSettingsHash { get; set; } = string.Empty;
    public HumanoidConformanceMappingMode MappingMode { get; set; }
    /// <summary>Required only when <see cref="MappingMode"/> is <see cref="HumanoidConformanceMappingMode.PersistedCorrection"/>.</summary>
    public string MappingCorrectionsSourceFileId { get; set; } = string.Empty;
    public bool HasConventionalBoneNames { get; set; }
    public bool HasArbitraryBoneNames { get; set; }
    public bool HasDistinctBindAxesAndProportions { get; set; }
    public bool HasMissingOptionalRoles { get; set; }
    public bool IsIntegrationOnly { get; set; }
    public List<string> CompatibleClipIds { get; set; } = [];
    public HumanoidConformanceCoordinateSpaces CoordinateSpaces { get; set; } = new();
}
