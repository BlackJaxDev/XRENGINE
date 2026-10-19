using System.Numerics;
using System.Text.RegularExpressions;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    /// <summary>
    /// Converts the supported opaque Unity Standard surface to the engine's exact PBR texture family.
    /// </summary>
    private static bool TryConvertOpaqueStandardMaterial(
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        List<string> warnings,
        out XRMaterial material)
    {
        material = null!;
        string? shaderPath = resolver.Resolve(document.Shader.Guid);
        if (string.IsNullOrWhiteSpace(shaderPath) || !File.Exists(shaderPath))
            return false;

        string shaderSource;
        try
        {
            shaderSource = File.ReadAllText(shaderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not read Unity shader for material '{document.Name}': {ex.Message}");
            return false;
        }

        // Use the declared shader name. A file name or saved property list cannot prove the shader family.
        if (!Regex.IsMatch(shaderSource, "(?m)^\\s*Shader\\s+\"Standard\"\\s*(?:\\{|$)", RegexOptions.CultureInvariant))
            return false;

        if (!IsSupportedOpaqueStandardState(document, out string reason))
        {
            warnings.Add($"Unity Standard material '{document.Name}' used generic import: {reason}");
            return false;
        }

        SerializedTextureImportDocument? normalSettings = document.ValidKeywords.Contains("_NORMALMAP")
            ? GetSourceTextureSettings(document, resolver, "_BumpMap")
            : null;
        if (document.ValidKeywords.Contains("_NORMALMAP") &&
            (normalSettings is null || !normalSettings.IsNormalMap || normalSettings.ConvertsToNormalMap ||
             normalSettings.IsSrgb || normalSettings.FlipGreenChannel || normalSettings.Shape != SerializedTextureShape.Texture2D))
        {
            warnings.Add($"Unity Standard material '{document.Name}' used generic import: the source normal map must be a linear RGB normal texture without green-channel flip or height conversion.");
            return false;
        }

        XRTexture2D? baseTexture = ResolveGenericTexture(document, resolver, warnings, "_MainTex");
        if (baseTexture is null)
        {
            warnings.Add($"Unity Standard material '{document.Name}' has no resolved _MainTex for opaque PBR texture import.");
            return false;
        }

        XRTexture2D? normalTexture = document.ValidKeywords.Contains("_NORMALMAP")
            ? ResolveGenericTexture(document, resolver, warnings, "_BumpMap")
            : null;
        if (document.ValidKeywords.Contains("_NORMALMAP") && normalTexture is null)
        {
            warnings.Add($"Unity Standard material '{document.Name}' has no resolved _BumpMap; normal mapping was not converted.");
            return false;
        }

        if (normalTexture is not null &&
            (normalTexture.ImportedColorSpace != ETextureColorSpace.Linear ||
             normalTexture.ImportedUsage != ETextureImportUsage.Normal ||
             normalTexture.ImportedNormalMapFlipGreen ||
             normalTexture.SizedInternalFormat is ESizedInternalFormat.Srgb8 or ESizedInternalFormat.Srgb8Alpha8))
        {
            warnings.Add($"Unity Standard material '{document.Name}' used generic import: the loaded normal texture does not match its linear RGB source settings.");
            return false;
        }

        SerializedTextureImportDocument? baseSettings = GetSourceTextureSettings(document, resolver, "_MainTex");
        if (resolver.ImportContext is null && baseSettings is not null &&
            baseTexture.ImportedColorSpace != (baseSettings.IsSrgb ? ETextureColorSpace.Srgb : ETextureColorSpace.Linear))
        {
            warnings.Add($"Unity Standard material '{document.Name}' used generic import: the loaded albedo texture does not match its source color space.");
            return false;
        }

        Vector4 sourceColor = document.TryGetVector("_Color", out Vector4 color)
            ? color
            : Vector4.One;
        if (ReadSourceLinearColorSpace(resolver.ProjectRoot))
        {
            sourceColor = new Vector4(
                SourceToonColorSpaceNormalizer.GammaToLinear(sourceColor.X),
                SourceToonColorSpaceNormalizer.GammaToLinear(sourceColor.Y),
                SourceToonColorSpaceNormalizer.GammaToLinear(sourceColor.Z),
                sourceColor.W);
        }
        float metallic = document.TryGetFloat("_Metallic", out float sourceMetallic) ? sourceMetallic : 0;
        float smoothness = document.TryGetFloat("_Glossiness", out float sourceSmoothness) ? sourceSmoothness : 0.5f;
        material = XRMaterial.CreateLitPbrTextureMaterial(
            baseTexture,
            normal: normalTexture,
            tint: new ColorF4(sourceColor.X, sourceColor.Y, sourceColor.Z, sourceColor.W),
            specular: 1,
            roughnessFactor: 1 - smoothness,
            metallicFactor: metallic);
        material.Name = document.Name;
        warnings.Add($"Unity Standard material '{document.Name}' uses the engine's opaque PBR lighting and culling. Source shader pass-specific lighting and culling are not converted.");
        return true;
    }

    private static bool IsSupportedOpaqueStandardState(SerializedMaterialDocument document, out string reason)
    {
        reason = "unsupported Standard state or feature";
        if (document.CustomRenderQueue != -1 ||
            document.TryGetFloat("_Mode", out float mode) && mode != 0 ||
            document.TryGetFloat("_SrcBlend", out float srcBlend) && srcBlend != 1 ||
            document.TryGetFloat("_DstBlend", out float dstBlend) && dstBlend != 0 ||
            document.TryGetFloat("_ZWrite", out float zWrite) && zWrite != 1 ||
            document.TryGetVector("_Color", out Vector4 color) &&
                (color.W != 1 || !float.IsFinite(color.X) || !float.IsFinite(color.Y) || !float.IsFinite(color.Z)) ||
            document.TryGetFloat("_Metallic", out float metallic) && (!float.IsFinite(metallic) || metallic < 0 || metallic > 1) ||
            document.TryGetFloat("_Glossiness", out float smoothness) && (!float.IsFinite(smoothness) || smoothness < 0 || smoothness > 1) ||
            document.TryGetFloat("_BumpScale", out float bumpScale) && bumpScale != 1 ||
            document.TryGetFloat("_SpecularHighlights", out float specularHighlights) && specularHighlights != 1 ||
            document.TryGetFloat("_GlossyReflections", out float glossyReflections) && glossyReflections != 1 ||
            document.TryGetFloat("_SmoothnessTextureChannel", out float smoothnessChannel) && smoothnessChannel != 0 ||
            document.TryGetVector("_EmissionColor", out Vector4 emissionColor) &&
                (emissionColor.X != 0 || emissionColor.Y != 0 || emissionColor.Z != 0))
            return false;

        foreach (string keyword in document.ValidKeywords)
        {
            if (keyword != "_NORMALMAP")
            {
                reason = $"keyword '{keyword}' is not supported by opaque Standard conversion";
                return false;
            }
        }

        if (document.OverrideTags.Count != 0 || document.DisabledShaderPasses.Count != 0)
            return false;

        if (!HasIdentityUv(document, "_MainTex") || !HasIdentityUv(document, "_BumpMap"))
        {
            reason = "texture UV transforms are not supported by the opaque PBR texture contract";
            return false;
        }

        foreach (string property in new[]
                 {
                     "_MetallicGlossMap", "_OcclusionMap", "_ParallaxMap", "_EmissionMap",
                     "_DetailMask", "_DetailAlbedoMap", "_DetailNormalMap"
                 })
        {
            if (document.Textures.TryGetValue(property, out SerializedTexturePropertyDocument? texture) &&
                texture.TextureReference.HasExternalGuid)
            {
                reason = $"texture '{property}' is not supported by opaque Standard conversion";
                return false;
            }
        }

        if (document.ValidKeywords.Contains("_NORMALMAP") &&
            (!document.Textures.TryGetValue("_BumpMap", out SerializedTexturePropertyDocument? bump) ||
             !bump.TextureReference.HasExternalGuid))
        {
            reason = "_NORMALMAP is enabled without a referenced _BumpMap";
            return false;
        }

        return true;
    }

    private static bool HasIdentityUv(SerializedMaterialDocument document, string property)
        => !document.Textures.TryGetValue(property, out SerializedTexturePropertyDocument? texture) ||
           texture.Scale == Vector2.One && texture.Offset == Vector2.Zero;

    private static SerializedTextureImportDocument? GetSourceTextureSettings(
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        string property)
    {
        if (!document.Textures.TryGetValue(property, out SerializedTexturePropertyDocument? texture))
            return null;

        string? path = resolver.Resolve(texture.TextureReference.Guid);
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
            ? null
            : SerializedTextureImportDocumentParser.ParseFile(path);
    }
}
