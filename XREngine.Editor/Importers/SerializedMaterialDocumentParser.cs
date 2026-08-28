using System.Globalization;
using System.Numerics;
using YamlDotNet.RepresentationModel;

namespace XREngine.Scene.Importers;

/// <summary>
/// Parses Unity material YAML independently from any destination shader converter.
/// </summary>
public static class SerializedMaterialDocumentParser
{
    private static readonly HashSet<string> KnownRootFields =
    [
        "serializedVersion",
        "m_Name",
        "m_Shader",
        "m_Parent",
        "m_ModifiedSerializedProperties",
        "m_ValidKeywords",
        "m_InvalidKeywords",
        "m_ShaderKeywords",
        "m_LightmapFlags",
        "m_EnableInstancingVariants",
        "m_DoubleSidedGI",
        "m_CustomRenderQueue",
        "stringTagMap",
        "m_StringTagMap",
        "disabledShaderPasses",
        "m_DisabledShaderPasses",
        "m_LockedProperties",
        "m_SavedProperties",
        "m_BuildTextureStacks",
        "m_AllowLocking",
    ];

    private static readonly HashSet<string> KnownSavedPropertyFields =
    [
        "serializedVersion",
        "m_TexEnvs",
        "m_Ints",
        "m_Floats",
        "m_Colors",
        "m_Vectors",
        "m_Strings",
    ];

    public static SerializedMaterialDocument ParseFile(string materialPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(materialPath);
        string normalizedPath = Path.GetFullPath(materialPath);
        return Parse(File.ReadAllText(normalizedPath), normalizedPath);
    }

    public static SerializedMaterialDocument Parse(string yamlText, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(yamlText);
        YamlMappingNode mapping = SerializedAssetYamlReader.LoadDocumentMapping(yamlText, "Material")
            ?? throw new InvalidDataException("Unity YAML did not contain a Material document.");

        var document = new SerializedMaterialDocument
        {
            Name = SerializedAssetYamlReader.GetScalarString(mapping, "m_Name") ??
                   Path.GetFileNameWithoutExtension(sourcePath ?? string.Empty),
            SourcePath = sourcePath,
            RawYaml = yamlText,
            SerializedVersion = SerializedAssetYamlReader.GetScalarInt(mapping, "serializedVersion"),
            Shader = SerializedAssetYamlReader.ParseReference(SerializedAssetYamlReader.GetNode(mapping, "m_Shader")),
            CustomRenderQueue = SerializedAssetYamlReader.GetScalarInt(mapping, "m_CustomRenderQueue") ?? -1,
        };

        ParseKeywords(SerializedAssetYamlReader.GetNode(mapping, "m_ShaderKeywords"), document.ValidKeywords);
        ParseKeywords(SerializedAssetYamlReader.GetNode(mapping, "m_ValidKeywords"), document.ValidKeywords);
        ParseKeywords(SerializedAssetYamlReader.GetNode(mapping, "m_InvalidKeywords"), document.InvalidKeywords);
        ParseStringSequence(
            SerializedAssetYamlReader.GetNode(mapping, "m_DisabledShaderPasses") ??
            SerializedAssetYamlReader.GetNode(mapping, "disabledShaderPasses"),
            document.DisabledShaderPasses);
        ParseStringMap(
            SerializedAssetYamlReader.GetNode(mapping, "m_StringTagMap") ??
            SerializedAssetYamlReader.GetNode(mapping, "stringTagMap"),
            document.OverrideTags);

        foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
        {
            string? key = (keyNode as YamlScalarNode)?.Value;
            if (!string.IsNullOrWhiteSpace(key) && !KnownRootFields.Contains(key))
                document.UnknownSerializedFields[key] = SerializedAssetYamlReader.PreserveNode(valueNode);
        }

        if (SerializedAssetYamlReader.GetNode(mapping, "m_SavedProperties") is not YamlMappingNode savedProperties)
            return document;

        document.SavedPropertiesSerializedVersion =
            SerializedAssetYamlReader.GetScalarInt(savedProperties, "serializedVersion");

        ParseTextureProperties(SerializedAssetYamlReader.GetNode(savedProperties, "m_TexEnvs"), document.Textures);
        ParseFloatProperties(SerializedAssetYamlReader.GetNode(savedProperties, "m_Floats"), document.Floats);
        ParseIntProperties(SerializedAssetYamlReader.GetNode(savedProperties, "m_Ints"), document.Ints);
        ParseVectorProperties(SerializedAssetYamlReader.GetNode(savedProperties, "m_Colors"), document.Vectors);
        ParseVectorProperties(SerializedAssetYamlReader.GetNode(savedProperties, "m_Vectors"), document.Vectors);
        ParseStringProperties(SerializedAssetYamlReader.GetNode(savedProperties, "m_Strings"), document.Strings);

        foreach ((YamlNode keyNode, YamlNode valueNode) in savedProperties.Children)
        {
            string? key = (keyNode as YamlScalarNode)?.Value;
            if (!string.IsNullOrWhiteSpace(key) && !KnownSavedPropertyFields.Contains(key))
                document.UnknownSavedProperties[key] = SerializedAssetYamlReader.PreserveNode(valueNode);
        }

        return document;
    }

    private static void ParseKeywords(YamlNode? node, HashSet<string> destination)
    {
        if (node is YamlScalarNode scalar)
        {
            foreach (string keyword in (scalar.Value ?? string.Empty).Split(
                         [' ', '\t', '\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                destination.Add(keyword);
            }
            return;
        }

        ParseStringSequence(node, destination);
    }

    private static void ParseStringSequence(YamlNode? node, HashSet<string> destination)
    {
        if (node is not YamlSequenceNode sequence)
            return;

        foreach (YamlNode child in sequence.Children)
        {
            string? value = (child as YamlScalarNode)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
                destination.Add(value);
        }
    }

    private static void ParseStringMap(YamlNode? node, Dictionary<string, string> destination)
    {
        if (node is not YamlMappingNode mapping)
            return;

        foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
        {
            string? key = (keyNode as YamlScalarNode)?.Value;
            if (!string.IsNullOrWhiteSpace(key))
                destination[key] = (valueNode as YamlScalarNode)?.Value ?? SerializedAssetYamlReader.PreserveNode(valueNode);
        }
    }

    private static void ParseTextureProperties(
        YamlNode? node,
        Dictionary<string, SerializedTexturePropertyDocument> destination)
    {
        if (node is not YamlSequenceNode sequence)
            return;

        foreach (YamlNode item in sequence.Children)
        {
            if (item is not YamlMappingNode entry)
                continue;

            foreach ((YamlNode keyNode, YamlNode valueNode) in entry.Children)
            {
                string? propertyName = (keyNode as YamlScalarNode)?.Value;
                if (string.IsNullOrWhiteSpace(propertyName) || valueNode is not YamlMappingNode textureNode)
                    continue;

                destination[propertyName] = new SerializedTexturePropertyDocument(
                    SerializedAssetYamlReader.ParseReference(SerializedAssetYamlReader.GetNode(textureNode, "m_Texture")),
                    SerializedAssetYamlReader.GetVector2(textureNode, "m_Scale", Vector2.One),
                    SerializedAssetYamlReader.GetVector2(textureNode, "m_Offset", Vector2.Zero));
            }
        }
    }

    private static void ParseFloatProperties(YamlNode? node, Dictionary<string, float> destination)
    {
        if (node is not YamlSequenceNode sequence)
            return;

        foreach ((string propertyName, YamlNode valueNode) in EnumeratePropertyEntries(sequence))
        {
            if (float.TryParse(
                    (valueNode as YamlScalarNode)?.Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float value))
            {
                destination[propertyName] = value;
            }
        }
    }

    private static void ParseIntProperties(YamlNode? node, Dictionary<string, int> destination)
    {
        if (node is not YamlSequenceNode sequence)
            return;

        foreach ((string propertyName, YamlNode valueNode) in EnumeratePropertyEntries(sequence))
        {
            if (int.TryParse(
                    (valueNode as YamlScalarNode)?.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int value))
            {
                destination[propertyName] = value;
            }
        }
    }

    private static void ParseVectorProperties(YamlNode? node, Dictionary<string, Vector4> destination)
    {
        if (node is not YamlSequenceNode sequence)
            return;

        foreach ((string propertyName, YamlNode valueNode) in EnumeratePropertyEntries(sequence))
        {
            if (valueNode is YamlMappingNode vectorNode)
                destination[propertyName] = SerializedAssetYamlReader.GetVector4(vectorNode, Vector4.Zero);
        }
    }

    private static void ParseStringProperties(YamlNode? node, Dictionary<string, string> destination)
    {
        if (node is not YamlSequenceNode sequence)
            return;

        foreach ((string propertyName, YamlNode valueNode) in EnumeratePropertyEntries(sequence))
            destination[propertyName] = (valueNode as YamlScalarNode)?.Value ?? SerializedAssetYamlReader.PreserveNode(valueNode);
    }

    private static IEnumerable<(string PropertyName, YamlNode Value)> EnumeratePropertyEntries(
        YamlSequenceNode sequence)
    {
        foreach (YamlNode item in sequence.Children)
        {
            if (item is not YamlMappingNode entry)
                continue;

            foreach ((YamlNode keyNode, YamlNode valueNode) in entry.Children)
            {
                string? propertyName = (keyNode as YamlScalarNode)?.Value;
                if (!string.IsNullOrWhiteSpace(propertyName))
                    yield return (propertyName, valueNode);
            }
        }
    }
}
