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

    private static readonly IReadOnlySet<string> LockedShaderHashes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "4c94ba9ab54f9efb79eb230b2cadeddb4563750408b511c25126a495a6dedf20",
            "7b2364410f93af8e23592f7311e99a23a805a05d11ddfbbd25dce0bebd7f1277",
            "69cf7629839f82963b56b32db3f77f223515c7561d907a1bb23fa8864d13895d",
            "fef23425a3e1916bf624c2cc11ac74345aaf799d6b15282a7bc9b5c9c6d2e297",
            "0ea1a3c48ed561548d432a66eba0545aa85873c4964f92a8fe66fcbea09c73f4",
            "d31d5dd0d03bfa2bf5db11cf348d13b79186d748d71dbf2569d7cc40dfbb283f",
            "5d42aeb1a5c15467dfd23ddce7ffd85441f2f3f0758519a524295ee4a7e5a4f1",
            "f09d10dcae522409ad1027a800edb5d8198173c01bfb804ad7922fc2fb599e1d",
            "c2068748239d31fad44e8124429def52bb3c09d5a10e8d4e39b25c4a42a0d266",
            "32ed0966dd5153383f222b37be7e55877ff45751966b00310be90b0f5bdd89fa",
            "6c29ed03ec73603e66d9561c7c52303d6f51509d04a5bc3c3f7aa3e6a6b1ef8b",
            "a808b1283f73b0ef056a56d98a31922b1b7bd3e8f91ff6c66e90be3590325234",
            "2b390f9faeec0f1b1a527478d6ab41f5ffb8c3c5acc328415466a715a24913b0",
            "bb38d6072fb806019294afebc0a60344d4198be0ed087ceb52d049568a8b0e88",
            "caf1a3b892e72c3ab25cb8b1921b494d6c49603cc32f7d204a479f1bf326fade",
        };

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

        string normalizedSource = input.ShaderSource.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string sourceHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSource)));
        if (!isLocked)
            return string.Equals(input.ShaderGuid, OriginalShaderGuid, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(sourceHash, ShaderSha256, StringComparison.Ordinal);

        return input.OverrideTags.TryGetValue("OriginalShaderGUID", out string? originalGuid) &&
               string.Equals(originalGuid, OriginalShaderGuid, StringComparison.OrdinalIgnoreCase) &&
               LockedShaderHashes.Contains(sourceHash);
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
