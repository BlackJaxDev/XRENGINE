namespace XREngine.Components.Animation;

/// <summary>Versioned, Unity-free declaration of a humanoid animation conformance corpus.</summary>
public sealed class HumanoidConformanceManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string CorpusId { get; set; } = string.Empty;
    public string CorpusVersion { get; set; } = string.Empty;
    public string Provenance { get; set; } = string.Empty;
    public bool RequiresUnityInstallation { get; set; }
    public HumanoidConformanceCapability RequiredCoverage { get; set; }
    public List<HumanoidConformanceSourceFile> SourceFiles { get; set; } = [];
    public List<HumanoidConformanceCaptureTool> CaptureTools { get; set; } = [];
    public List<HumanoidConformanceAssetCheck> AssetChecks { get; set; } = [];
    public List<HumanoidConformanceAvatar> Avatars { get; set; } = [];
    public List<HumanoidConformanceClip> Clips { get; set; } = [];
    public List<HumanoidConformanceMatrixCase> Matrix { get; set; } = [];
}
