namespace XREngine.Scene.Prefabs;

/// <summary>
/// Structured diagnostic retained on a generated Unity prefab asset.
/// </summary>
[Serializable]
public sealed class SourceImportDiagnostic
{
    public string Code { get; set; } = string.Empty;
    public SourceImportDiagnosticSeverity Severity { get; set; }
    public SourceImportDiagnosticCategory Category { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? SourcePath { get; set; }
    public string? PropertyPath { get; set; }
    public SourceAssetIdentity? SourceIdentity { get; set; }

    public override string ToString()
        => $"[{Code}] {Message}";
}
