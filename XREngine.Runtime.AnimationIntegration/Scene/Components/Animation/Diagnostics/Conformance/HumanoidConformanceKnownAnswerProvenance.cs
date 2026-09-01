namespace XREngine.Components.Animation;

/// <summary>Machine-readable provenance binding a known-answer capture to its exact source inputs and capture contract.</summary>
public sealed class HumanoidConformanceKnownAnswerProvenance
{
    /// <summary>Identifier of the content-addressed capture tool declared by the corpus.</summary>
    public string CaptureToolId { get; set; } = string.Empty;
    public string SourceUnityEditorVersion { get; set; } = string.Empty;
    public string SerializedClipVersion { get; set; } = string.Empty;
    public int ReferenceSchemaVersion { get; set; }
    public string SourceAvatarSha256 { get; set; } = string.Empty;
    public string SourceClipSha256 { get; set; } = string.Empty;
    public string AvatarDefinitionSignature { get; set; } = string.Empty;
    public string AvatarImportSettingsSha256 { get; set; } = string.Empty;
    public string ClipImportSettingsSha256 { get; set; } = string.Empty;
    public string CaptureToolIdentity { get; set; } = string.Empty;
    public string CaptureToolVersion { get; set; } = string.Empty;
    public string CaptureToolSha256 { get; set; } = string.Empty;
    public HumanoidConformanceCoordinateSpaces CoordinateSpaces { get; set; } = new();
    public HumanoidConformanceTolerances Tolerances { get; set; } = new();
}
