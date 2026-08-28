namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Source evidence used to identify unlocked and optimizer-generated Poiyomi shaders.
/// </summary>
public sealed record SourceToonShaderMatchInput
{
    public string? ShaderPath { get; init; }
    public string? ShaderGuid { get; init; }
    public string? ShaderSource { get; init; }
    public IReadOnlySet<string> PropertyNames { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> OverrideTags { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
