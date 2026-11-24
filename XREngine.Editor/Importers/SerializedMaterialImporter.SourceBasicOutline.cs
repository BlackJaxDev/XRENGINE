using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    private static bool RequiresSourceBasicOutline(
        SerializedMaterialDocument document,
        SourceToonShaderMatchResult match)
        => match.SourceFamily == SourceToonShaderFamily.Pro &&
           match.IsDowngradeSource &&
           document.TryGetPositive("_EnableOutlines") &&
           !document.DisabledShaderPasses.Contains("Outline");

    private static void ApplySourceBasicOutline(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceToonShaderMatchResult match)
    {
        if (!match.IsLocked)
            throw new SourceVisualImportException("Active source toon Basic outline requires a verified locked source shader.");
        // 9.3.9 and 9.3.63 compute the Basic outline offset and color with the same math. 9.3.63
        // adds only an outline Z offset, which the checks below require to be zero.
        if (match.Version != new SourceToonShaderVersion(9, 3, 63) &&
            match.Version != new SourceToonShaderVersion(9, 3, 9))
            throw new SourceVisualImportException(
                $"Active source toon outline requires verified source version 9.3.9 or 9.3.63; found {match.Version?.ToString() ?? "unknown"}.");

        RequireSourceBasicOutlineValue(document, "_OutlineExpansionMode", 1.0f);
        RequireSourceBasicOutlineValue(document, "_OutlineSpace", 0.0f);
        RequireSourceBasicOutlineValue(document, "_OutlineMaskUV", 0.0f);
        RequireSourceBasicOutlineValue(document, "_OutlineMaskChannel", 0.0f);
        RequireSourceBasicOutlineValue(document, "_OutlineClipAtZeroWidth", 1.0f);
        RequireSourceBasicOutlineValue(document, "_OutlineUseVertexColorNormals", 0.0f);
        RequireSourceBasicOutlineValue(document, "_OutlineVertexColorMask", 0.0f);
        RequireSourceBasicOutlineValue(document, "_OutlineAlphaDistanceFade", 0.0f);
        RequireSourceBasicOutlineValue(document, "_OutlineOverrideAlpha", 0.0f);
        RequireSourceBasicOutlineValue(document, "_Offset_Z", 0.0f);
        RequireSourceBasicOutlineValue(document, "_LineColorThemeIndex", 0.0f);
        RequireSourceBasicOutlineValue(document, "_OutlineHueShift", 0.0f);
        RequireSourceBasicOutlineValue(document, "_OutlineTintMix", 0.0f);
        RequireSourceBasicOutlineValue(document, "_OutlineALColorEnabled", 0.0f);

        bool audioLinkEnabled = document.TryGetPositive("_EnableAudioLink") ||
                                document.ValidKeywords.Contains("POI_AUDIOLINK");
        if (audioLinkEnabled && document.TryGetVector("_AudioLinkOutlineSize", out Vector4 audioWidth) &&
            audioWidth != Vector4.Zero)
            throw new SourceVisualImportException(
                $"Active source Basic outline AudioLink width '_AudioLinkOutlineSize={audioWidth}' has no exact conversion.");

        float fixedSize = RequireFiniteSourceBasicOutlineValue(document, "_OutlineFixedSize");
        if (fixedSize is not (0.0f or 1.0f))
            throw new SourceVisualImportException($"Active source outline option '_OutlineFixedSize={fixedSize}' requires 0 or 1.");

        float width = RequireFiniteSourceBasicOutlineValue(document, "_LineWidth");
        float fixWidth = RequireFiniteSourceBasicOutlineValue(document, "_OutlineFixWidth");
        float maxDistance = RequireFiniteSourceBasicOutlineValue(document, "_OutlinesMaxDistance");
        if (width <= 0.0f || fixWidth < 0.0f || fixWidth > 1.0f || maxDistance <= 0.0f)
            throw new SourceVisualImportException(
                $"Active source outline width/distance requires _LineWidth>0, 0<=_OutlineFixWidth<=1, and _OutlinesMaxDistance>0; values are {width}, {fixWidth}, {maxDistance}.");

        document.Textures.TryGetValue("_OutlineMask", out SerializedTexturePropertyDocument? mask);
        bool externalMask = mask?.TextureReference.HasExternalGuid == true;
        if (mask is not null && !externalMask && mask.TextureReference.FileId != 0)
            throw new SourceVisualImportException("Active source Basic outline mask has an unsupported internal texture reference.");
        if (mask is not null && (mask.Scale != Vector2.One || mask.Offset != Vector2.Zero))
            throw new SourceVisualImportException(
                $"Active source Basic outline mask transform requires identity scale and zero offset; _OutlineMask scale={mask.Scale}, offset={mask.Offset}.");
        if (document.TryGetVector("_OutlineMaskPan", out Vector4 pan) && pan != Vector4.Zero)
            throw new SourceVisualImportException($"Active source Basic outline requires zero _OutlineMaskPan; value={pan}.");

        XRTexture2D? outlineMask = null;
        foreach (XRTexture? texture in material.Textures)
        {
            if (texture is XRTexture2D candidate && candidate.SamplerName == "_OutlineMask")
            {
                outlineMask = candidate;
                break;
            }
        }

        bool usesDefaultWhiteMask = outlineMask is not null &&
                                    IsDefaultSamplerTexture(outlineMask, "_OutlineMask");
        if (outlineMask is null || (externalMask && usesDefaultWhiteMask))
            throw new SourceVisualImportException("Active source Basic outline external '_OutlineMask' did not resolve.");
        if (!externalMask && !usesDefaultWhiteMask)
            throw new SourceVisualImportException("Active source Basic outline default '_OutlineMask' did not resolve to white.");
        if (externalMask && (outlineMask.UWrap != ETexWrapMode.Repeat ||
            outlineMask.VWrap != ETexWrapMode.Repeat ||
            outlineMask.MagFilter != ETexMagFilter.Linear ||
            outlineMask.MinFilter is not (ETexMinFilter.Linear or ETexMinFilter.LinearMipmapNearest or ETexMinFilter.LinearMipmapLinear) ||
            outlineMask.LodBias != 0.0f))
            throw new SourceVisualImportException(
                "Active source Basic outline requires a resolved linear-repeat '_OutlineMask' sampler with zero LOD bias.");

        SetInt(material, "_OutlineBasicModel", 1);
        SetFloat(material, "_OutlineFixWidth", fixWidth);
        SetFloat(material, "_OutlinesMaxDistance", maxDistance);
    }

    private static void RequireSourceBasicOutlineValue(
        SerializedMaterialDocument document,
        string name,
        float expected)
    {
        float value = RequireFiniteSourceBasicOutlineValue(document, name);
        if (value != expected)
            throw new SourceVisualImportException(
                $"Active source Basic outline option '{name}={value}' requires {expected} for exact conversion.");
    }

    private static float RequireFiniteSourceBasicOutlineValue(
        SerializedMaterialDocument document,
        string name)
    {
        if (!document.TryGetFloat(name, out float value) || !float.IsFinite(value))
            throw new SourceVisualImportException($"Active source Basic outline option '{name}' requires a finite value.");
        return value;
    }
}
