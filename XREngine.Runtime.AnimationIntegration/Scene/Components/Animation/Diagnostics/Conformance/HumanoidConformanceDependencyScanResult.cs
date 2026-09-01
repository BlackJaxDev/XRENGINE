namespace XREngine.Components.Animation;

/// <summary>Read-only production dependency scan result.</summary>
public sealed class HumanoidConformanceDependencyScanResult
{
    public List<string> ScannedRoots { get; set; } = [];
    public int ScannedFileCount { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<HumanoidConformanceDependencyFinding> Findings { get; set; } = [];
    public bool Passed => Errors.Count == 0
        && Findings.Count == 0
        && ScannedRoots.Count > 0
        && ScannedFileCount > 0;
}
