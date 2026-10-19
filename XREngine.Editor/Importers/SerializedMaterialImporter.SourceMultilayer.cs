using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    private static void BindSourceMultilayerParameters(
        XRMaterial material,
        SerializedMaterialDocument document)
    {
        int maskType = GetInt(document, "_ShadowMaskType");
        if (maskType is not (0 or 1))
            throw new SourceVisualImportException(
                $"Source multilayer shadow mask type {maskType} is not supported on '{document.Name}'.");

        SetVector4(material, "_ShadowColor", SourceMultilayerColor(document, "_ShadowColor",
            new Vector4(0.7f, 0.75f, 0.85f, 1.0f)));
        SetVector4(material, "_Shadow2ndColor", SourceMultilayerColor(document, "_Shadow2ndColor",
            Vector4.Zero));
        SetVector4(material, "_Shadow3rdColor", SourceMultilayerColor(document, "_Shadow3rdColor",
            Vector4.Zero));
        SetVector4(material, "_ShadowBorderColor", SourceMultilayerColor(document, "_ShadowBorderColor",
            new Vector4(1.0f, 0.0f, 0.0f, 1.0f)));
        SetVector4(material, "_SourceMultilayerBorders", new Vector4(
            GetFloat(document, "_ShadowBorder", 0.5f),
            GetFloat(document, "_Shadow2ndBorder", 0.5f),
            GetFloat(document, "_Shadow3rdBorder", 0.25f),
            GetFloat(document, "_ShadowBorderRange")));
        SetVector4(material, "_SourceMultilayerBlurs", new Vector4(
            GetFloat(document, "_ShadowBlur", 0.1f),
            GetFloat(document, "_Shadow2ndBlur", 0.3f),
            GetFloat(document, "_Shadow3rdBlur", 0.1f),
            GetFloat(document, "_LightingMulitlayerNonLinear", 1.0f)));
        SetVector4(material, "_SourceMultilayerReceive", new Vector4(
            GetFloat(document, "_ShadowReceive"),
            GetFloat(document, "_Shadow2ndReceive"),
            GetFloat(document, "_Shadow3rdReceive"),
            GetFloat(document, "_ShadowMainStrength")));
        SetVector4(material, "_SourceMultilayerMix", new Vector4(
            GetFloat(document, "_ShadowEnvStrength"),
            GetFloat(document, "_ShadowStrength", 1.0f),
            GetFloat(document, "_LightingIgnoreAmbientColor"),
            GetInt(document, "_LightingMapMode") == 4 ? 1.0f : 0.0f));
        SetVector4(material, "_SourceMultilayerOptions", new Vector4(
            HasExternalTexture(document, "_ShadowBorderMask")
                ? GetFloat(document, "_ShadowBorderMapToggle") : 0.0f,
            maskType,
            GetFloat(document, "_ShadowPostAO"),
            GetFloat(document, "_ShadowBorderMaskLOD")));
        SetVector4(material, "_SourceMultilayerFlat", new Vector4(
            GetFloat(document, "_ShadowFlatBorder", 1.0f),
            GetFloat(document, "_ShadowFlatBlur", 1.0f), 0.0f, 0.0f));
        // Unity scene conversion reflects the source object Z axis.
        SetVector4(material, "_SourceMultilayerFlatAxis", new Vector4(0.0f, 0.25f, -1.0f, 0.0f));
        SetVector4(material, "_ShadowAOShift", SourceMultilayerColor(document,
            "_ShadowAOShift", new Vector4(0.0f, 1.0f, 0.0f, 1.0f)));
        SetVector4(material, "_ShadowAOShift2", SourceMultilayerColor(document,
            "_ShadowAOShift2", new Vector4(0.0f, 1.0f, 0.0f, 1.0f)));
    }

    private static Vector4 SourceMultilayerColor(
        SerializedMaterialDocument document, string name, Vector4 fallback)
        => document.TryGetVector(name, out Vector4 value) ? value : fallback;

    private static void BindSourceMultilayerTextures(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        List<string> warnings)
    {
        if (GetInt(document, "_LightingMode") != 1)
            return;

        BindSourceMultilayerTexture(material, document, resolver, diagnostics, warnings,
            "_ShadowColorTex");
        BindSourceMultilayerTexture(material, document, resolver, diagnostics, warnings,
            "_Shadow2ndColorTex");
        BindSourceMultilayerTexture(material, document, resolver, diagnostics, warnings,
            "_Shadow3rdColorTex");
        BindSourceMultilayerTexture(material, document, resolver, diagnostics, warnings,
            "_MultilayerMathBlurMap");
        BindSourceMultilayerTexture(material, document, resolver, diagnostics, warnings,
            "_ShadowStrengthMask");
        BindSourceMultilayerBorderTexture(material, document, resolver, warnings,
            "_ShadowBorderMask", ETexMinFilter.LinearMipmapNearest);
        BindSourceMultilayerBorderTexture(material, document, resolver, warnings,
            "_SourceShadowBorderMaskLod", ETexMinFilter.LinearMipmapLinear);
        ApplySourceToonTextureTransform(material, document, "_ShadowBorderMask",
            "_ShadowBorderMask", null, diagnostics);
    }

    private static void BindSourceMultilayerBorderTexture(
        XRMaterial material, SerializedMaterialDocument document, SourceAssetResolver resolver,
        List<string> warnings, string sampler, ETexMinFilter minFilter)
    {
        // The source uses fixed repeat samplers, not the texture import filter.
        XRTexture2D texture = ResolveUberTexture(document, resolver, warnings,
            "_ShadowBorderMask", sampler, sourceRepeatMinFilter: minFilter)
            ?? GetDefaultUberSamplerTexture(sampler, ColorF4.White);
        texture.MagFilter = ETexMagFilter.Linear;
        texture.MinFilter = minFilter;
        texture.UWrap = ETexWrapMode.Repeat;
        texture.VWrap = ETexWrapMode.Repeat;
        ReplaceMaterialSampler(material, sampler, texture);
    }

    private static void BindSourceMultilayerTexture(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        List<string> warnings,
        string sampler)
    {
        if (!HasSampler(material.Textures, sampler))
        {
            XRTexture2D texture = ResolveUberTexture(document, resolver, warnings, sampler, sampler)
                ?? GetDefaultUberSamplerTexture(sampler, ColorF4.White);
            ReplaceMaterialSampler(material, sampler, texture);
        }
        ApplySourceToonTextureTransform(material, document, sampler, sampler, null, diagnostics);
    }
}
