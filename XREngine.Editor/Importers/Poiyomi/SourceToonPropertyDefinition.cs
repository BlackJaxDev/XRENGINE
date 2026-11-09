namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Retains a verified ShaderLab property type, default, and enum declaration.
/// </summary>
public sealed record SourceToonPropertyDefinition
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string DefaultLiteral { get; init; }
    public string? EnumDeclaration { get; init; }
    public IReadOnlyList<SourceToonPropertyDefinition> AlternateDefinitions { get; init; } = [];
}
