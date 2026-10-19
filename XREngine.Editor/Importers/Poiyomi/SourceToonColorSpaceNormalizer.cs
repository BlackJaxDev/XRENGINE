using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Converts serialized color properties to the values used by the source shader.
/// </summary>
public static partial class SourceToonColorSpaceNormalizer
{
    private static readonly IReadOnlyDictionary<string, bool> CatalogConversions = LoadCatalogConversions();

    /// <summary>
    /// Converts RGB in a private copy. Alpha, vectors, and source metadata stay unchanged.
    /// HDR colors with Gamma and ordinary colors without Gamma use the source linear transfer.
    /// </summary>
    public static SerializedMaterialDocument Normalize(
        SerializedMaterialDocument source,
        string? shaderPath,
        bool linearColorSpace,
        SourceToonShaderVersion? version = null)
    {
        if (!linearColorSpace)
            return source;

        // Older sources use their own declarations. Do not apply 9.3 color flags to them.
        Dictionary<string, bool> conversions = SourceToon92Catalog.Supports(version) || version == SourceToon81Catalog.Version
            ? new(StringComparer.Ordinal)
            : new(CatalogConversions, StringComparer.Ordinal);
        if (shaderPath is not null && File.Exists(shaderPath))
        {
            foreach (Match declaration in PropertyDeclaration().Matches(File.ReadAllText(shaderPath)))
            {
                string attributes = declaration.Groups["attributes"].Value;
                bool hdr = HdrAttribute().IsMatch(attributes);
                bool gamma = GammaAttribute().IsMatch(attributes);
                conversions[declaration.Groups["name"].Value] =
                    declaration.Groups["type"].Value == "Color" && hdr == gamma;
            }
        }

        SerializedMaterialDocument? result = null;
        foreach ((string name, Vector4 value) in source.Vectors)
        {
            if (!conversions.TryGetValue(name, out bool convert) || !convert)
                continue;

            result ??= source.CreateConversionCopy();
            result.Vectors[name] = new Vector4(
                GammaToLinear(value.X), GammaToLinear(value.Y), GammaToLinear(value.Z), value.W);
        }
        return result ?? source;
    }

    /// <summary>
    /// Uses the source engine's exact sRGB transfer, including negative and HDR inputs.
    /// </summary>
    public static float GammaToLinear(float value)
        => value <= 0.04045f ? value / 12.92f
            : value < 1.0f ? MathF.Pow((value + 0.055f) / 1.055f, 2.4f)
            : MathF.Pow(value, 2.2f);

    private static IReadOnlyDictionary<string, bool> LoadCatalogConversions()
    {
        using Stream stream = SourceToon93Catalog.OpenCatalog();
        using JsonDocument catalog = JsonDocument.Parse(stream);
        Dictionary<string, bool> conversions = new(StringComparer.Ordinal);
        foreach (JsonElement property in catalog.RootElement.GetProperty("properties").EnumerateArray())
        {
            bool hdr = false;
            bool gamma = false;
            foreach (JsonElement attribute in property.GetProperty("attributes").EnumerateArray())
            {
                string? name = attribute.GetProperty("name").GetString();
                hdr |= name == "HDR";
                gamma |= name == "Gamma";
            }
            conversions.Add(property.GetProperty("name").GetString()!,
                property.GetProperty("type").GetString() == "Color" && hdr == gamma);
        }
        return conversions;
    }

    // Display labels can contain escaped quotes. Match declarations, not shader variable names.
    [GeneratedRegex("(?m)^[ \\t]*(?<attributes>(?:\\[[^\\]\\r\\n]*\\][ \\t]*)*)(?<name>\\w+)[ \\t]*\\([ \\t]*\"(?:\\\\.|[^\"\\\\\\r\\n])*\"[ \\t]*,[ \\t]*(?<type>Color|Vector|Float|Range)\\b", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyDeclaration();

    [GeneratedRegex(@"\[HDR\]", RegexOptions.CultureInvariant)]
    private static partial Regex HdrAttribute();

    [GeneratedRegex(@"\[Gamma\]", RegexOptions.CultureInvariant)]
    private static partial Regex GammaAttribute();
}
