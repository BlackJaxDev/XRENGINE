namespace XREngine.Scene.Prefabs;

/// <summary>
/// Structured diagnostic retained on a generated Unity prefab asset.
/// </summary>
[Serializable]
public sealed class UnityImportDiagnostic
{
    public string Code { get; set; } = string.Empty;
    public UnityImportDiagnosticSeverity Severity { get; set; }
    public UnityImportDiagnosticCategory Category { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? SourcePath { get; set; }
    public string? PropertyPath { get; set; }
    public UnityAssetIdentity? SourceIdentity { get; set; }

    public override string ToString()
        => $"[{Code}] {Message}";
}
