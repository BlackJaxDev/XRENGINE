using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Identifies verified Poiyomi Toon 10.0.25 source and locked shader variants.
/// </summary>
public static partial class SourceToon10Catalog
{
    public static readonly SourceToonShaderVersion Version = new(10, 0, 25);
    public const string OriginalShaderGuid = "e9f30272e2d12c945851329f504df73d";
    public const string ShaderPathSegment = "/_PoiyomiShaders/Shaders/10.0/Toon/";
    public const string ShaderSha256 = "36395208f10bcb430cf0548d558f47de319b36f35e1ef8b623eef38b09ba6291";

    private static readonly IReadOnlySet<string> ConversionDefaultProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "_Mode", "_Cull", "_Cutoff", "_AlphaToCoverage", "_ZWrite", "_ZTest",
            "_SrcBlend", "_DstBlend", "_SrcBlendAlpha", "_DstBlendAlpha", "_BlendOp",
            "_ShadingEnabled", "_LightingMode", "_MainAlphaMaskMode", "_MainTexUV",
            "_BumpScale", "_LightDataAOStrengthR", "_LightDataAOStrengthG",
            "_LightDataAOStrengthB", "_LightDataAOStrengthA",
        };

    /// <summary>
    /// Match a complete shader against a verified source identity.
    /// </summary>
    public static bool Matches(SourceToonShaderMatchInput input, SourceToonShaderVersion? version, bool isLocked)
    {
        if (version != Version || input.ShaderSource is null)
            return false;

        // The optimizer writes new locked text each time it locks a material, so locked
        // text has no stable hash. Its identity is the exact version and original shader.
        if (isLocked)
            return input.OverrideTags.TryGetValue("OriginalShaderGUID", out string? originalGuid) &&
                   string.Equals(originalGuid, OriginalShaderGuid, StringComparison.OrdinalIgnoreCase);

        string normalizedSource = input.ShaderSource.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string sourceHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSource)));
        return string.Equals(input.ShaderGuid, OriginalShaderGuid, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(sourceHash, ShaderSha256, StringComparison.Ordinal);
    }

    /// <summary>
    /// Read property definitions from an exact verified shader source.
    /// </summary>
    public static IReadOnlyDictionary<string, SourceToonPropertyDefinition> ReadPropertyDefinitions(string shaderSource)
    {
        ArgumentNullException.ThrowIfNull(shaderSource);

        int start = shaderSource.IndexOf("Properties", StringComparison.Ordinal);
        int end = start < 0 ? -1 : shaderSource.IndexOf("SubShader", start, StringComparison.Ordinal);
        if (start < 0 || end < 0)
            throw new InvalidDataException("Verified shader has no complete Properties block.");

        ReadOnlySpan<char> properties = shaderSource.AsSpan(start, end - start);
        Dictionary<string, SourceToonPropertyDefinition> definitions = new(StringComparer.Ordinal);
        foreach (Match match in PropertyStartRegex().Matches(properties.ToString()))
        {
            string name = match.Groups["name"].Value;
            int cursor = match.Index + match.Length;
            if (!SkipQuotedText(properties, ref cursor) || !SkipToType(properties, ref cursor))
                throw new InvalidDataException($"Verified shader property '{name}' has an invalid label or type.");

            int typeStart = cursor;
            int depth = 1;
            while (cursor < properties.Length && depth > 0)
            {
                if (properties[cursor] == '(')
                    depth++;
                else if (properties[cursor] == ')')
                    depth--;

                cursor++;
            }

            if (depth != 0)
                throw new InvalidDataException($"Verified shader property '{name}' has an unterminated type.");

            string type = properties[typeStart..(cursor - 1)].Trim().ToString();
            SkipWhiteSpace(properties, ref cursor);
            if (cursor >= properties.Length || properties[cursor++] != '=')
                throw new InvalidDataException($"Verified shader property '{name}' has no default value.");

            int defaultStart = cursor;
            while (cursor < properties.Length && properties[cursor] is not ('\r' or '\n'))
                cursor++;

            string defaultLiteral = properties[defaultStart..cursor].Trim().ToString();
            string attributes = properties.Slice(match.Index, match.Groups["name"].Index - match.Index).ToString();
            SourceToonPropertyDefinition definition = new()
            {
                Name = name,
                Type = type,
                DefaultLiteral = defaultLiteral,
                EnumDeclaration = ReadEnumDeclaration(attributes),
            };
            if (definitions.TryGetValue(name, out SourceToonPropertyDefinition? existing))
                definitions[name] = existing with
                {
                    AlternateDefinitions = [.. existing.AlternateDefinitions, definition],
                };
            else
                definitions.Add(name, definition);
        }

        if (definitions.Count == 0)
            throw new InvalidDataException("Verified shader has no readable property definitions.");

        return definitions;
    }

    /// <summary>
    /// Apply verified converter defaults to a copy of the source material.
    /// </summary>
    public static SerializedMaterialDocument CreateConversionDocument(
        SerializedMaterialDocument source,
        SourceToonShaderMatchResult match)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(match);
        if (!match.IsAccepted || match.Version != Version)
            throw new ArgumentException("A verified Toon 10.0.25 match is required.", nameof(match));

        SerializedMaterialDocument result = source.CreateConversionCopy();
        foreach (string name in ConversionDefaultProperties)
        {
            if (result.HasAnyProperty(name) || !match.PropertyDefinitions.TryGetValue(name, out SourceToonPropertyDefinition? definition))
                continue;

            if (definition.AlternateDefinitions.Any(alternate =>
                !string.Equals(alternate.DefaultLiteral, definition.DefaultLiteral, StringComparison.Ordinal)))
                continue;

            string literal = definition.DefaultLiteral;
            if (definition.Type is "Color" or "Vector")
            {
                if (TryReadVector(literal, out Vector4 value))
                    result.Vectors.Add(name, value);
            }
            else if (definition.Type == "Int")
            {
                if (float.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) &&
                    float.IsFinite(value) && value >= int.MinValue && value <= int.MaxValue &&
                    value == MathF.Round(value))
                    result.Ints.Add(name, (int)value);
            }
            else if (definition.Type == "Float" || definition.Type.StartsWith("Range(", StringComparison.Ordinal))
            {
                if (float.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value))
                    result.Floats.Add(name, value);
            }
        }

        return result;
    }

    private static bool TryReadVector(string literal, out Vector4 value)
    {
        value = default;
        if (literal.Length < 2 || literal[0] != '(' || literal[^1] != ')')
            return false;

        string[] components = literal[1..^1].Split(',', StringSplitOptions.TrimEntries);
        if (components.Length != 4 ||
            !float.TryParse(components[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(components[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
            !float.TryParse(components[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) ||
            !float.TryParse(components[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float w))
            return false;

        value = new Vector4(x, y, z, w);
        return true;
    }

    private static bool SkipQuotedText(ReadOnlySpan<char> source, ref int cursor)
    {
        while (cursor < source.Length)
        {
            if (source[cursor] == '\\')
            {
                cursor += 2;
                continue;
            }

            if (source[cursor++] == '"')
                return true;
        }

        return false;
    }

    private static bool SkipToType(ReadOnlySpan<char> source, ref int cursor)
    {
        SkipWhiteSpace(source, ref cursor);
        if (cursor >= source.Length || source[cursor++] != ',')
            return false;

        SkipWhiteSpace(source, ref cursor);
        return cursor < source.Length;
    }

    private static void SkipWhiteSpace(ReadOnlySpan<char> source, ref int cursor)
    {
        while (cursor < source.Length && char.IsWhiteSpace(source[cursor]))
            cursor++;
    }

    private static string? ReadEnumDeclaration(string attributes)
    {
        int start = attributes.IndexOf("ThryWideEnum(", StringComparison.Ordinal);
        if (start < 0)
            start = attributes.IndexOf("KeywordEnum(", StringComparison.Ordinal);
        if (start < 0)
            start = attributes.IndexOf("Enum(", StringComparison.Ordinal);
        if (start < 0)
            return null;

        int cursor = attributes.IndexOf('(', start);
        int depth = 0;
        for (; cursor < attributes.Length; cursor++)
        {
            if (attributes[cursor] == '(')
                depth++;
            else if (attributes[cursor] == ')' && --depth == 0)
                return attributes[start..(cursor + 1)];
        }

        throw new InvalidDataException("Verified shader has an unterminated enum declaration.");
    }

    [GeneratedRegex(@"(?m)^[ \t]*(?:\[[^\]\r\n]*\][ \t]*)*(?<name>[A-Za-z_][A-Za-z_0-9]*)[ \t]*\([ \t]*""")]
    private static partial Regex PropertyStartRegex();
}
