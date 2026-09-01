namespace XREngine.Components.Animation;

/// <summary>A hashed, manifest-root-relative source or known-answer file.</summary>
public sealed class HumanoidConformanceSourceFile
{
    public string Id { get; set; } = string.Empty;
    public HumanoidConformanceArtifactKind ArtifactKind { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string Provenance { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}
