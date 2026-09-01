namespace XREngine.Components.Animation;

/// <summary>
/// Versioned, content-addressed correction for an imported humanoid fixture.
/// The correction is deliberately independent of avatar display names so it can
/// be reused by any imported hierarchy with the declared structural paths.
/// </summary>
public sealed class HumanoidConformanceMappingCorrection
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string FixtureVersion { get; set; } = string.Empty;
    public string Fixture { get; set; } = string.Empty;
    public string MappingMode { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string SourceFbxSha256 { get; set; } = string.Empty;
    public string ExpectedAvatarDefinitionSignature { get; set; } = string.Empty;
    public Dictionary<string, string> Roles { get; set; } = [];
}
