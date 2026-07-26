using YamlDotNet.RepresentationModel;

namespace XREngine.Scene.Importers;

/// <summary>
/// Parses rendering-relevant TextureImporter settings from Unity .meta YAML.
/// </summary>
public static class UnityTextureImportDocumentParser
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

    public static UnityTextureImportDocument? ParseFile(string texturePath)
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

    public static UnityTextureImportDocument Parse(string yamlText, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(yamlText);
        YamlMappingNode mapping = UnityYamlReader.LoadDocumentMapping(yamlText, "TextureImporter")
            ?? throw new InvalidDataException("Unity YAML did not contain a TextureImporter document.");

        YamlMappingNode? mipmaps = UnityYamlReader.GetNode(mapping, "mipmaps") as YamlMappingNode;
        YamlMappingNode? bumpmap = UnityYamlReader.GetNode(mapping, "bumpmap") as YamlMappingNode;
        YamlMappingNode? settings = UnityYamlReader.GetNode(mapping, "textureSettings") as YamlMappingNode;

        int textureType = UnityYamlReader.GetScalarInt(mapping, "textureType") ?? 0;
        int rawShape = UnityYamlReader.GetScalarInt(mapping, "textureShape") ?? 1;
        bool isSrgb =
            UnityYamlReader.GetScalarBool(mapping, "sRGBTexture") ??
            (mipmaps is null ? null : UnityYamlReader.GetScalarBool(mipmaps, "sRGBTexture")) ??
            !(UnityYamlReader.GetScalarBool(mapping, "linearTexture") ?? false);

        Dictionary<string, string> unknown = new(StringComparer.Ordinal);
        foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
        {
            string? key = (keyNode as YamlScalarNode)?.Value;
            if (!string.IsNullOrWhiteSpace(key) && !KnownFields.Contains(key))
                unknown[key] = UnityYamlReader.PreserveNode(valueNode);
        }

        return new UnityTextureImportDocument
        {
            SourcePath = sourcePath,
            RawYaml = yamlText,
            SerializedVersion = UnityYamlReader.GetScalarInt(mapping, "serializedVersion"),
            IsSrgb = isSrgb,
            TextureType = textureType,
            IsNormalMap = textureType == 1 ||
                          (bumpmap is not null &&
                           (UnityYamlReader.GetScalarBool(bumpmap, "convertToNormalMap") ?? false)),
            NormalMapChannel =
                UnityYamlReader.GetScalarInt(mapping, "singleChannelComponent") ??
                (bumpmap is null ? 0 : UnityYamlReader.GetScalarInt(bumpmap, "normalMapFilter") ?? 0),
            FlipGreenChannel =
                bumpmap is not null && (UnityYamlReader.GetScalarBool(bumpmap, "flipGreenChannel") ?? false),
            AlphaSource =
                UnityYamlReader.GetScalarInt(mapping, "alphaSource") ??
                UnityYamlReader.GetScalarInt(mapping, "alphaUsage") ??
                0,
            AlphaIsTransparency =
                UnityYamlReader.GetScalarBool(mapping, "alphaIsTransparency") ?? false,
            WrapU = settings is null ? 0 : UnityYamlReader.GetScalarInt(settings, "wrapU") ?? 0,
            WrapV = settings is null ? 0 : UnityYamlReader.GetScalarInt(settings, "wrapV") ?? 0,
            WrapW = settings is null ? 0 : UnityYamlReader.GetScalarInt(settings, "wrapW") ?? 0,
            FilterMode = settings is null ? 1 : UnityYamlReader.GetScalarInt(settings, "filterMode") ?? 1,
            GenerateMipMaps =
                mipmaps is null || UnityYamlReader.GetScalarBool(mipmaps, "enableMipMap") is not false,
            MipBias = settings is null ? 0.0f : UnityYamlReader.GetScalarFloat(settings, "mipBias") ?? 0.0f,
            Anisotropy = settings is null ? 1 : UnityYamlReader.GetScalarInt(settings, "aniso") ?? 1,
            RawShape = rawShape,
            Shape = Enum.IsDefined(typeof(UnityTextureShape), rawShape)
                ? (UnityTextureShape)rawShape
                : UnityTextureShape.Unknown,
            UnknownSerializedFields = unknown,
        };
    }
}
