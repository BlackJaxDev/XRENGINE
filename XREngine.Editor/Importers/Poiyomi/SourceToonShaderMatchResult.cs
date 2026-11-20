namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Result of matching source shader evidence against the pinned Poiyomi catalog.
/// </summary>
public sealed record SourceToonShaderMatchResult
{
    public required SourceToonShaderMatchKind Kind { get; init; }
    public SourceToonShaderVersion? Version { get; init; }

    /// <summary>
    /// The verified version whose conversion rules apply. It is the same as <see cref="Version"/> for an exact
    /// identity, and the behavior version of the release line for an admitted release that is not verified.
    /// </summary>
    public SourceToonShaderVersion? BehaviorVersion { get; init; }

    /// <summary>The release line that admitted the shader, or null for an exact identity.</summary>
    public SourceToonReleaseLine? ReleaseLine { get; init; }
    public SourceToonShaderFamily SourceFamily { get; init; }
    public bool IsSourceToonFamily { get; init; }
    public bool IsAccepted { get; init; }
    public bool IsDowngradeSource { get; init; }
    public bool IsLocked { get; init; }
    public IReadOnlyDictionary<string, SourceToonPropertyDefinition> PropertyDefinitions { get; init; } =
        new Dictionary<string, SourceToonPropertyDefinition>(StringComparer.Ordinal);
    public IReadOnlyList<MaterialConversionDiagnostic> Diagnostics { get; init; } = [];
}
