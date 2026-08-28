using YamlDotNet.RepresentationModel;

namespace XREngine.Scene.Importers;

/// <summary>
/// Parses rendering-relevant TextureImporter settings from Unity .meta YAML.
/// </summary>
public static class SerializedTextureImportDocumentParser
{
    private static readonly HashSet<string> KnownFields =
    [
        "serializedVersion",
        "mipmaps",
        "bumpmap",
        "isReadable",
        "streamingMipmaps",
        "streamingMipmapsPriority",
        "vTOnly",
        "ignoreMipmapLimit",
        "grayScaleToAlpha",
        "generateCubemap",
        "cubemapConvolution",
        "seamlessCubemap",
        "textureFormat",
        "maxTextureSize",
        "textureSettings",
        "nPOTScale",
        "lightmap",
        "compressionQuality",
        "spriteMode",
        "spriteExtrude",
        "spriteMeshType",
        "alignment",
        "spritePivot",
        "spritePixelsToUnits",
        "spriteBorder",
        "spriteGenerateFallbackPhysicsShape",
        "alphaUsage",
        "alphaSource",
        "alphaIsTransparency",
        "spriteTessellationDetail",
        "textureType",
        "textureShape",
        "singleChannelComponent",
        "flipbookRows",
        "flipbookColumns",
        "maxTextureSizeSet",
        "compressionQualitySet",
        "textureFormatSet",
        "ignorePngGamma",
        "applyGammaDecoding",
        "swizzle",
        "cookieLightType",
        "platformSettings",
        "spriteSheet",
        "mipmapLimitGroupName",
        "pSDRemoveMatte",
        "userData",
        "assetBundleName",
        "assetBundleVariant",
        "sRGBTexture",
        "linearTexture",
    ];

    public static SerializedTextureImportDocument? ParseFile(string texturePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(texturePath);
        string normalizedPath = Path.GetFullPath(texturePath);
        string metaPath = normalizedPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
            ? normalizedPath
            : normalizedPath + ".meta";
        if (!File.Exists(metaPath))
            return null;

        try
        {
            return Parse(File.ReadAllText(metaPath), metaPath);
        }
        catch (InvalidDataException)
        {
            // Folder, shader, and legacy sidecar metadata do not contain TextureImporter settings.
            return null;
        }
    }

    public static SerializedTextureImportDocument Parse(string yamlText, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(yamlText);
        YamlMappingNode mapping = SerializedAssetYamlReader.LoadDocumentMapping(yamlText, "TextureImporter")
            ?? throw new InvalidDataException("Unity YAML did not contain a TextureImporter document.");

        YamlMappingNode? mipmaps = SerializedAssetYamlReader.GetNode(mapping, "mipmaps") as YamlMappingNode;
        YamlMappingNode? bumpmap = SerializedAssetYamlReader.GetNode(mapping, "bumpmap") as YamlMappingNode;
        YamlMappingNode? settings = SerializedAssetYamlReader.GetNode(mapping, "textureSettings") as YamlMappingNode;

        int textureType = SerializedAssetYamlReader.GetScalarInt(mapping, "textureType") ?? 0;
        int rawShape = SerializedAssetYamlReader.GetScalarInt(mapping, "textureShape") ?? 1;
        bool isSrgb =
            SerializedAssetYamlReader.GetScalarBool(mapping, "sRGBTexture") ??
            (mipmaps is null ? null : SerializedAssetYamlReader.GetScalarBool(mipmaps, "sRGBTexture")) ??
            !(SerializedAssetYamlReader.GetScalarBool(mapping, "linearTexture") ?? false);

        Dictionary<string, string> unknown = new(StringComparer.Ordinal);
        foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
        {
            string? key = (keyNode as YamlScalarNode)?.Value;
            if (!string.IsNullOrWhiteSpace(key) && !KnownFields.Contains(key))
                unknown[key] = SerializedAssetYamlReader.PreserveNode(valueNode);
        }

        return new SerializedTextureImportDocument
        {
            SourcePath = sourcePath,
            RawYaml = yamlText,
            SerializedVersion = SerializedAssetYamlReader.GetScalarInt(mapping, "serializedVersion"),
            IsSrgb = isSrgb,
            TextureType = textureType,
            IsNormalMap = textureType == 1 ||
                          (bumpmap is not null &&
                           (SerializedAssetYamlReader.GetScalarBool(bumpmap, "convertToNormalMap") ?? false)),
            NormalMapChannel =
                SerializedAssetYamlReader.GetScalarInt(mapping, "singleChannelComponent") ??
                (bumpmap is null ? 0 : SerializedAssetYamlReader.GetScalarInt(bumpmap, "normalMapFilter") ?? 0),
            FlipGreenChannel =
                bumpmap is not null && (SerializedAssetYamlReader.GetScalarBool(bumpmap, "flipGreenChannel") ?? false),
            AlphaSource =
                SerializedAssetYamlReader.GetScalarInt(mapping, "alphaSource") ??
                SerializedAssetYamlReader.GetScalarInt(mapping, "alphaUsage") ??
                0,
            AlphaIsTransparency =
                SerializedAssetYamlReader.GetScalarBool(mapping, "alphaIsTransparency") ?? false,
            WrapU = settings is null ? 0 : SerializedAssetYamlReader.GetScalarInt(settings, "wrapU") ?? 0,
            WrapV = settings is null ? 0 : SerializedAssetYamlReader.GetScalarInt(settings, "wrapV") ?? 0,
            WrapW = settings is null ? 0 : SerializedAssetYamlReader.GetScalarInt(settings, "wrapW") ?? 0,
            FilterMode = settings is null ? 1 : SerializedAssetYamlReader.GetScalarInt(settings, "filterMode") ?? 1,
            GenerateMipMaps =
                mipmaps is null || SerializedAssetYamlReader.GetScalarBool(mipmaps, "enableMipMap") is not false,
            MipBias = settings is null ? 0.0f : SerializedAssetYamlReader.GetScalarFloat(settings, "mipBias") ?? 0.0f,
            Anisotropy = settings is null ? 1 : SerializedAssetYamlReader.GetScalarInt(settings, "aniso") ?? 1,
            RawShape = rawShape,
            FlipbookRows = Math.Max(1, SerializedAssetYamlReader.GetScalarInt(mapping, "flipbookRows") ?? 1),
            FlipbookColumns = Math.Max(1, SerializedAssetYamlReader.GetScalarInt(mapping, "flipbookColumns") ?? 1),
            Shape = Enum.IsDefined(typeof(SerializedTextureShape), rawShape)
                ? (SerializedTextureShape)rawShape
                : SerializedTextureShape.Unknown,
            UnknownSerializedFields = unknown,
        };
    }
}
