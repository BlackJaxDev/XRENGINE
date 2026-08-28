using System.Numerics;

namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Source-independent, versioned Poiyomi material semantics shared by conversion,
/// reporting, reimport, animation, and authoring workflows.
/// </summary>
public sealed record SourceToonMaterialDescriptor
{
    public required string Name { get; init; }
    public required SourceToonShaderVersion Version { get; init; }
    public bool IsLocked { get; init; }
    public required SerializedMaterialDocument SourceDocument { get; init; }
    public required SourceResolvedAsset ShaderAsset { get; init; }
    public IReadOnlyDictionary<string, SourceToonPropertyBinding> PropertyBindings { get; init; } =
        new Dictionary<string, SourceToonPropertyBinding>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, SourceToonTextureDescriptor> Textures { get; init; } =
        new Dictionary<string, SourceToonTextureDescriptor>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, float> Floats { get; init; } =
        new Dictionary<string, float>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, int> Ints { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, Vector4> Vectors { get; init; } =
        new Dictionary<string, Vector4>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> Strings { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlySet<string> ValidKeywords { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> InvalidKeywords { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> DisabledShaderPasses { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> OverrideTags { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
