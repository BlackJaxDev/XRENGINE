namespace XREngine.Scene.Importers.Poiyomi;

/// <summary>
/// Result of matching source shader evidence against the pinned Poiyomi catalog.
/// </summary>
public sealed record PoiyomiShaderMatchResult
{
    public required PoiyomiShaderMatchKind Kind { get; init; }
    public PoiyomiShaderVersion? Version { get; init; }
    public bool IsPoiyomiFamily { get; init; }
    public bool IsAccepted { get; init; }
    public bool IsLocked { get; init; }
    public IReadOnlyList<MaterialConversionDiagnostic> Diagnostics { get; init; } = [];
}
