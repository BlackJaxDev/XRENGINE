using System.Numerics;

namespace XREngine.Scene.Importers;

/// <summary>
/// Lossless source representation of a serialized Unity material.
/// </summary>
public sealed class UnityMaterialDocument
{
    public string Name { get; init; } = string.Empty;
    public string? SourcePath { get; init; }
    public string RawYaml { get; init; } = string.Empty;
    public int? SerializedVersion { get; init; }
    public int? SavedPropertiesSerializedVersion { get; set; }
    public UnityAssetReference Shader { get; init; }
    public int CustomRenderQueue { get; init; } = -1;
    public HashSet<string> ValidKeywords { get; } = new(StringComparer.Ordinal);
    public HashSet<string> InvalidKeywords { get; } = new(StringComparer.Ordinal);
    public HashSet<string> DisabledShaderPasses { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> OverrideTags { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, UnityTexturePropertyDocument> Textures { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, float> Floats { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Ints { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Vector4> Vectors { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Strings { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> UnknownSerializedFields { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> UnknownSavedProperties { get; } = new(StringComparer.Ordinal);

    public bool HasAnyProperty(string name)
        => Textures.ContainsKey(name) ||
           Floats.ContainsKey(name) ||
           Ints.ContainsKey(name) ||
           Vectors.ContainsKey(name) ||
           Strings.ContainsKey(name);

    public IReadOnlySet<string> GetPropertyNames()
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        names.UnionWith(Textures.Keys);
        names.UnionWith(Floats.Keys);
        names.UnionWith(Ints.Keys);
        names.UnionWith(Vectors.Keys);
        names.UnionWith(Strings.Keys);
        return names;
    }

    public bool TryGetFloat(string name, out float value)
    {
        if (Floats.TryGetValue(name, out value))
            return true;

        if (Ints.TryGetValue(name, out int intValue))
        {
            value = intValue;
            return true;
        }

        value = 0.0f;
        return false;
    }

    public bool TryGetInt(string name, out int value)
    {
        if (Ints.TryGetValue(name, out value))
            return true;

        if (Floats.TryGetValue(name, out float floatValue))
        {
            value = (int)MathF.Round(floatValue);
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryGetVector(string name, out Vector4 value)
        => Vectors.TryGetValue(name, out value);

    public bool TryGetPositive(string name)
        => TryGetFloat(name, out float value) && value > 0.0001f;

    /// <summary>
    /// Returns the exact source text for diagnostic round trips.
    /// </summary>
    public string SerializeDiagnosticRoundTrip()
        => RawYaml;
}
