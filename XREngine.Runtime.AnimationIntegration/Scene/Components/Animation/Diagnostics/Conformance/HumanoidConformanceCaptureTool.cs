namespace XREngine.Components.Animation;

/// <summary>Content-addressed repository source for a known-answer capture tool.</summary>
public sealed class HumanoidConformanceCaptureTool
{
    public string Id { get; set; } = string.Empty;
    public string RelativeRepositoryPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Provenance { get; set; } = string.Empty;
}
