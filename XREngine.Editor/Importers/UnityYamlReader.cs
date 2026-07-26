using System.Globalization;
using System.Numerics;
using YamlDotNet.RepresentationModel;

namespace XREngine.Scene.Importers;

internal static class UnityYamlReader
{
    public static YamlMappingNode? LoadDocumentMapping(string yamlText, string documentType)
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(yamlText);
        yaml.Load(reader);

        foreach (YamlDocument document in yaml.Documents)
        {
            if (document.RootNode is not YamlMappingNode rootNode)
                continue;

            foreach ((YamlNode keyNode, YamlNode valueNode) in rootNode.Children)
            {
                if (string.Equals((keyNode as YamlScalarNode)?.Value, documentType, StringComparison.Ordinal) &&
                    valueNode is YamlMappingNode mappingNode)
                {
                    return mappingNode;
                }
            }
        }

        return null;
    }

    public static YamlNode? GetNode(YamlMappingNode mapping, string key)
    {
        foreach ((YamlNode yamlKey, YamlNode yamlValue) in mapping.Children)
        {
            if (string.Equals((yamlKey as YamlScalarNode)?.Value, key, StringComparison.Ordinal))
                return yamlValue;
        }

        return null;
    }

    public static string? GetScalarString(YamlMappingNode mapping, string key)
        => (GetNode(mapping, key) as YamlScalarNode)?.Value;

    public static int? GetScalarInt(YamlMappingNode mapping, string key)
        => int.TryParse(GetScalarString(mapping, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : null;

    public static long GetScalarLong(YamlMappingNode mapping, string key)
        => long.TryParse(GetScalarString(mapping, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out long result)
            ? result
            : 0L;

    public static float? GetScalarFloat(YamlMappingNode mapping, string key)
        => float.TryParse(GetScalarString(mapping, key), NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
            ? result
            : null;

    public static bool? GetScalarBool(YamlMappingNode mapping, string key)
    {
        string? value = GetScalarString(mapping, key);
        if (bool.TryParse(value, out bool boolean))
            return boolean;

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer)
            ? integer != 0
            : null;
    }

    public static Vector2 GetVector2(YamlMappingNode mapping, string key, Vector2 fallback)
    {
        if (GetNode(mapping, key) is not YamlMappingNode vectorMapping)
            return fallback;

        return new Vector2(
            GetScalarFloat(vectorMapping, "x") ?? fallback.X,
            GetScalarFloat(vectorMapping, "y") ?? fallback.Y);
    }

    public static Vector4 GetVector4(YamlMappingNode mapping, Vector4 fallback)
        => new(
            GetScalarFloat(mapping, "r") ?? GetScalarFloat(mapping, "x") ?? fallback.X,
            GetScalarFloat(mapping, "g") ?? GetScalarFloat(mapping, "y") ?? fallback.Y,
            GetScalarFloat(mapping, "b") ?? GetScalarFloat(mapping, "z") ?? fallback.Z,
            GetScalarFloat(mapping, "a") ?? GetScalarFloat(mapping, "w") ?? fallback.W);

    public static UnityAssetReference ParseReference(YamlNode? node)
    {
        if (node is not YamlMappingNode mapping)
            return default;

        return new UnityAssetReference(
            GetScalarLong(mapping, "fileID"),
            GetScalarString(mapping, "guid"),
            GetScalarInt(mapping, "type"));
    }

    public static string PreserveNode(YamlNode node)
    {
        var stream = new YamlStream(new YamlDocument(node));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }
}
