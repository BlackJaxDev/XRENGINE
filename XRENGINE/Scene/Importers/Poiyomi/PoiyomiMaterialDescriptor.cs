using System.Numerics;

namespace XREngine.Scene.Importers.Poiyomi;

/// <summary>
/// Source-independent, versioned Poiyomi material semantics used by later converter phases.
/// </summary>
public sealed record PoiyomiMaterialDescriptor
{
    public required string Name { get; init; }
    public required PoiyomiShaderVersion Version { get; init; }
    public bool IsLocked { get; init; }
    public required UnityMaterialDocument SourceDocument { get; init; }
    public required UnityResolvedAsset ShaderAsset { get; init; }
    public IReadOnlyDictionary<string, PoiyomiPropertyBinding> PropertyBindings { get; init; } =
        new Dictionary<string, PoiyomiPropertyBinding>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, PoiyomiTextureDescriptor> Textures { get; init; } =
        new Dictionary<string, PoiyomiTextureDescriptor>(StringComparer.Ordinal);
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
