namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Result of matching source shader evidence against the pinned Poiyomi catalog.
/// </summary>
public sealed record SourceToonShaderMatchResult
{
    public required SourceToonShaderMatchKind Kind { get; init; }
    public SourceToonShaderVersion? Version { get; init; }
    public SourceToonShaderFamily SourceFamily { get; init; }
    public bool IsSourceToonFamily { get; init; }
    public bool IsAccepted { get; init; }
    public bool IsDowngradeSource { get; init; }
    public bool IsLocked { get; init; }
    public IReadOnlyList<MaterialConversionDiagnostic> Diagnostics { get; init; } = [];
}
