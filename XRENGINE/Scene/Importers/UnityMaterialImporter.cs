using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using YamlDotNet.RepresentationModel;

namespace XREngine.Scene.Importers;

public sealed class UnityMaterialImportResult
{
    public XRMaterial? Material { get; init; }
    public bool IsPoiyomiToon { get; init; }
    public bool IsLilToon { get; init; }
    public string? ShaderPath { get; init; }
    public string[] Warnings { get; init; } = [];
}

public static class UnityMaterialImporter
{
    private static readonly ConcurrentDictionary<string, XRTexture2D> DefaultUberSamplerTextures = new(StringComparer.Ordinal);

    public static XRMaterial? Import(string materialPath, string? projectRoot = null)
        => ImportWithReport(materialPath, projectRoot).Material;

    public static UnityMaterialImportResult ImportWithReport(string materialPath, string? projectRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(materialPath);

        string normalizedPath = Path.GetFullPath(materialPath);
        List<string> warnings = [];

        if (LoadUnityDocumentMapping(normalizedPath, "Material") is not YamlMappingNode materialMapping)
        {
            warnings.Add($"Unity material '{normalizedPath}' did not contain a Material document.");
            return new UnityMaterialImportResult { Warnings = [.. warnings] };
        }

        UnityMaterialDocument document = ParseMaterialDocument(materialMapping, normalizedPath);
        var resolver = new UnityAssetResolver(projectRoot ?? ResolveUnityProjectRoot(normalizedPath));
        bool isPoiyomiToon = IsPoiyomiToon93Material(document, resolver, out string? shaderPath);
        bool isLilToon = false;
        if (!isPoiyomiToon)
            isLilToon = IsLilToonMaterial(document, resolver, out shaderPath);

        try
        {
            XRMaterial material = isPoiyomiToon
                ? ConvertPoiyomiToUberMaterial(document, resolver, warnings)
                : isLilToon
                    ? ConvertLilToonToUberMaterial(document, resolver, warnings, shaderPath)
                : ConvertGenericUnityMaterial(document, resolver, warnings);

            material.OriginalPath = normalizedPath;
            return new UnityMaterialImportResult
            {
                Material = material,
                IsPoiyomiToon = isPoiyomiToon,
                IsLilToon = isLilToon,
                ShaderPath = shaderPath,
                Warnings = [.. warnings],
            };
        }
        catch (Exception ex) when (isPoiyomiToon || isLilToon)
        {
            string shaderFamily = isLilToon ? "lilToon" : "Poiyomi";
            warnings.Add($"{shaderFamily} material conversion failed for '{document.Name}'. Falling back to generic Unity material import. {ex.Message}");
            XRMaterial material = ConvertGenericUnityMaterial(document, resolver, warnings);
            material.OriginalPath = normalizedPath;
            return new UnityMaterialImportResult
            {
                Material = material,
                IsPoiyomiToon = isPoiyomiToon,
                IsLilToon = isLilToon,
                ShaderPath = shaderPath,
                Warnings = [.. warnings],
            };
        }
    }

    private static XRMaterial ConvertPoiyomiToUberMaterial(
        UnityMaterialDocument document,
        UnityAssetResolver resolver,
        List<string> warnings)
    {
        XRTexture2D main = ResolveUberTexture(document, resolver, warnings, "_MainTex", "_MainTex")
            ?? GetDefaultUberSamplerTexture("_MainTex", ColorF4.White);

        XRTexture2D bump = ResolveUberTexture(document, resolver, warnings, "_BumpMap", "_BumpMap")
            ?? GetDefaultUberSamplerTexture("_BumpMap", new ColorF4(0.5f, 0.5f, 1.0f, 1.0f));

        float bumpScale = document.TryGetFloat("_BumpScale", out float authoredBumpScale)
            ? authoredBumpScale
            : ReferenceEquals(bump, DefaultUberSamplerTextures.GetOrAdd("_BumpMap", static key => CreateDefaultUberSamplerTexture(key, new ColorF4(0.5f, 0.5f, 1.0f, 1.0f)))) ? 0.0f : 1.0f;

        List<XRTexture?> textures = [main, bump];

        string? alphaSource = AddMappedTexture(document, resolver, warnings, textures, "_AlphaMask", "_AlphaMask", "_AlphaTexture");
        string? colorAdjustSource = AddMappedTexture(document, resolver, warnings, textures, "_MainColorAdjustTexture", "_MainColorAdjustTexture");
        string? shadowColorSource = AddMappedTexture(document, resolver, warnings, textures, "_ShadowColorTex", "_ShadowColorTex", "_ShadowColorTex", "_1st_ShadeMap", "_2nd_ShadeMap");
        string? aoSource = AddMappedTexture(document, resolver, warnings, textures, "_LightingAOMaps", "_LightingAOMaps");
        string? shadowMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_LightingShadowMasks", "_LightingShadowMasks");
        string? emissionSource = AddMappedTexture(document, resolver, warnings, textures, "_EmissionMap", "_EmissionMap");
        string? matcapSource = AddMappedTexture(document, resolver, warnings, textures, "_Matcap", "_Matcap");
        string? matcapMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_MatcapMask", "_MatcapMask", "_MatcapMask");
        string? rimMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_RimMask", "_RimMask", "_RimMask", "_RimTex", "_RimColorTex");
        string? specularSource = AddMappedTexture(document, resolver, warnings, textures, "_SpecularMap", "_SpecularMap", "_SpecularMap", "_MetallicGlossMap", "_SmoothnessTex");
        string? detailMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_DetailMask", "_DetailMask");
        string? detailTexSource = AddMappedTexture(document, resolver, warnings, textures, "_DetailTex", "_DetailTex");
        string? detailNormalSource = AddMappedTexture(document, resolver, warnings, textures, "_DetailNormalMap", "_DetailNormalMap");
        string? outlineMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_OutlineMask", "_OutlineMask", "_OutlineMask", "_OutlineTexture");
        string? backFaceSource = AddMappedTexture(document, resolver, warnings, textures, "_BackFaceTexture", "_BackFaceTexture");
        string? glitterMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_GlitterMask", "_GlitterMask");
        string? flipbookSource = AddMappedTexture(document, resolver, warnings, textures, "_FlipbookTexture", "_FlipbookTexture", "_FlipbookTexture", "_Flipbook");
        string? sssSource = AddMappedTexture(document, resolver, warnings, textures, "_SSSThicknessMap", "_SSSThicknessMap");
        string? dissolveSource = AddMappedTexture(document, resolver, warnings, textures, "_DissolveNoiseTexture", "_DissolveNoiseTexture", "_DissolveNoiseTexture", "_DissolveDetailNoise", "_DissolveMask");
        string? parallaxSource = AddMappedTexture(document, resolver, warnings, textures, "_ParallaxMap", "_ParallaxMap", "_ParallaxMap", "_ParallaxInternalMap");

        XRMaterial material = new()
        {
            Name = document.Name,
            Textures = [.. textures],
            Parameters = ModelImporter.CreateDefaultForwardPlusUberShaderParameters(bumpScale),
            RenderOptions = ModelImporter.CreateForwardPlusUberShaderRenderOptions(),
            RenderPass = (int)EDefaultRenderPass.OpaqueForward,
        };

        material.SetShader(EShaderType.Fragment, ShaderHelper.UberFragForward(), coerceShaderType: true);
        material.EnsureUberStateInitialized();

        ApplyTextureTransform(material, document, "_MainTex", "_MainTex");
        ApplyTextureTransform(material, document, "_BumpMap", "_BumpMap");
        ApplyTextureTransform(material, document, alphaSource, "_AlphaMask");
        ApplyTextureTransform(material, document, colorAdjustSource, "_MainColorAdjustTexture");
        ApplyTextureTransform(material, document, shadowColorSource, "_ShadowColorTex");
        ApplyTextureTransform(material, document, aoSource, "_LightingAOMaps");
        ApplyTextureTransform(material, document, shadowMaskSource, "_LightingShadowMasks");
        ApplyTextureTransform(material, document, emissionSource, "_EmissionMap");
        ApplyTextureTransform(material, document, matcapMaskSource, "_MatcapMask");
        ApplyTextureTransform(material, document, rimMaskSource, "_RimMask");
        ApplyTextureTransform(material, document, specularSource, "_SpecularMap");
        ApplyTextureTransform(material, document, detailMaskSource, "_DetailMask");
        ApplyTextureTransform(material, document, detailTexSource, "_DetailTex");
        ApplyTextureTransform(material, document, detailNormalSource, "_DetailNormalMap");
        ApplyTextureTransform(material, document, outlineMaskSource, "_OutlineMask");
        ApplyTextureTransform(material, document, dissolveSource, "_DissolveNoiseTexture");
        ApplyTextureTransform(material, document, parallaxSource, "_ParallaxMap");

        ApplyPoiyomiScalarAndColorProperties(material, document);
        ApplyPoiyomiFeatureState(material, document,
            alphaSource,
            colorAdjustSource,
            shadowColorSource,
            aoSource,
            shadowMaskSource,
            emissionSource,
            matcapSource,
            matcapMaskSource,
            rimMaskSource,
            specularSource,
            detailMaskSource,
            detailTexSource,
            detailNormalSource,
            outlineMaskSource,
            backFaceSource,
            glitterMaskSource,
            flipbookSource,
            sssSource,
            dissolveSource,
            parallaxSource);

        ApplyPoiyomiRenderState(material, document);
        material.PrepareUberVariantImmediately();
        return material;
    }

    private static XRMaterial ConvertLilToonToUberMaterial(
        UnityMaterialDocument document,
        UnityAssetResolver resolver,
        List<string> warnings,
        string? shaderPath)
    {
        XRTexture2D main = ResolveUberTexture(document, resolver, warnings, "_MainTex", "_MainTex")
            ?? ResolveUberTexture(document, resolver, warnings, "_BaseMap", "_MainTex")
            ?? GetDefaultUberSamplerTexture("_MainTex", ColorF4.White);

        XRTexture2D bump = ResolveUberTexture(document, resolver, warnings, "_BumpMap", "_BumpMap")
            ?? GetDefaultUberSamplerTexture("_BumpMap", new ColorF4(0.5f, 0.5f, 1.0f, 1.0f));

        float bumpScale = document.TryGetPositive("_UseBumpMap") || !IsDefaultSamplerTexture(bump, "_BumpMap")
            ? document.TryGetFloat("_BumpScale", out float authoredBumpScale) ? authoredBumpScale : 1.0f
            : 0.0f;

        List<XRTexture?> textures = [main, bump];

        string? alphaSource = AddMappedTexture(document, resolver, warnings, textures, "_AlphaMask", "_AlphaMask");
        string? colorAdjustSource = AddMappedTexture(document, resolver, warnings, textures, "_MainColorAdjustTexture", "_MainColorAdjustMask", "_MainGradationTex");
        string? shadowColorSource = AddMappedTexture(document, resolver, warnings, textures, "_ShadowColorTex", "_ShadowColorTex");
        string? aoSource = AddMappedTexture(document, resolver, warnings, textures, "_LightingAOMaps", "_ShadowStrengthMask");
        string? shadowMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_LightingShadowMasks", "_ShadowBorderMask", "_ShadowBlurMask");
        string? emissionSource = AddMappedTexture(document, resolver, warnings, textures, "_EmissionMap", "_EmissionMap");
        string? matcapSource = AddMappedTexture(document, resolver, warnings, textures, "_Matcap", "_MatCapTex");
        string? matcapMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_MatcapMask", "_MatCapBlendMask", "_MatCapBackfaceMask", "_MatCapShadowMask");
        string? rimMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_RimMask", "_RimColorTex");
        string? specularSource = AddMappedTexture(document, resolver, warnings, textures, "_SpecularMap", "_SmoothnessTex");
        string? detailMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_DetailMask", "_Main2ndBlendMask", "_Main3rdBlendMask");
        string? detailTexSource = AddMappedTexture(document, resolver, warnings, textures, "_DetailTex", "_Main2ndTex", "_Main3rdTex");
        string? detailNormalSource = AddMappedTexture(document, resolver, warnings, textures, "_DetailNormalMap", "_Bump2ndMap");
        string? outlineMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_OutlineMask", "_OutlineWidthMask", "_OutlineTex");
        string? backFaceSource = AddMappedTexture(document, resolver, warnings, textures, "_BackFaceTexture", "_BacklightColorTex");
        string? glitterMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_GlitterMask", "_GlitterColorTex", "_GlitterShapeTex");
        string? flipbookSource = null;
        string? sssSource = AddMappedTexture(document, resolver, warnings, textures, "_SSSThicknessMap", "_BacklightColorTex");
        string? dissolveSource = AddMappedTexture(document, resolver, warnings, textures, "_DissolveNoiseTexture", "_DissolveNoiseMask", "_DissolveMask");
        string? parallaxSource = AddMappedTexture(document, resolver, warnings, textures, "_ParallaxMap", "_ParallaxMap");

        XRMaterial material = new()
        {
            Name = document.Name,
            Textures = [.. textures],
            Parameters = ModelImporter.CreateDefaultForwardPlusUberShaderParameters(bumpScale),
            RenderOptions = ModelImporter.CreateForwardPlusUberShaderRenderOptions(),
            RenderPass = (int)EDefaultRenderPass.OpaqueForward,
        };

        material.SetShader(EShaderType.Fragment, ShaderHelper.UberFragForward(), coerceShaderType: true);
        material.EnsureUberStateInitialized();

        ApplyTextureTransform(material, document, "_MainTex", "_MainTex");
        ApplyTextureTransform(material, document, "_BaseMap", "_MainTex");
        ApplyTextureTransform(material, document, "_BumpMap", "_BumpMap");
        ApplyTextureTransform(material, document, alphaSource, "_AlphaMask");
        ApplyTextureTransform(material, document, colorAdjustSource, "_MainColorAdjustTexture");
        ApplyTextureTransform(material, document, shadowColorSource, "_ShadowColorTex");
        ApplyTextureTransform(material, document, aoSource, "_LightingAOMaps");
        ApplyTextureTransform(material, document, shadowMaskSource, "_LightingShadowMasks");
        ApplyTextureTransform(material, document, emissionSource, "_EmissionMap");
        ApplyTextureTransform(material, document, matcapMaskSource, "_MatcapMask");
        ApplyTextureTransform(material, document, rimMaskSource, "_RimMask");
        ApplyTextureTransform(material, document, specularSource, "_SpecularMap");
        ApplyTextureTransform(material, document, detailMaskSource, "_DetailMask");
        ApplyTextureTransform(material, document, detailTexSource, "_DetailTex");
        ApplyTextureTransform(material, document, detailNormalSource, "_DetailNormalMap");
        ApplyTextureTransform(material, document, outlineMaskSource, "_OutlineMask");
        ApplyTextureTransform(material, document, dissolveSource, "_DissolveNoiseTexture");
        ApplyTextureTransform(material, document, parallaxSource, "_ParallaxMap");

        ApplyLilToonScalarAndColorProperties(material, document);
        ApplyLilToonFeatureState(material, document,
            alphaSource,
            colorAdjustSource,
            shadowColorSource,
            aoSource,
            shadowMaskSource,
            emissionSource,
            matcapSource,
            matcapMaskSource,
            rimMaskSource,
            specularSource,
            detailMaskSource,
            detailTexSource,
            detailNormalSource,
            outlineMaskSource,
            backFaceSource,
            glitterMaskSource,
            flipbookSource,
            sssSource,
            dissolveSource,
            parallaxSource);

        ApplyLilToonRenderState(material, document, shaderPath);
        material.PrepareUberVariantImmediately();
        return material;
    }

    private static XRMaterial ConvertGenericUnityMaterial(
        UnityMaterialDocument document,
        UnityAssetResolver resolver,
        List<string> warnings)
    {
        ColorF4 baseColor = document.TryGetVector("_BaseColor", out Vector4 baseColorVector) ||
                            document.TryGetVector("_Color", out baseColorVector)
            ? new ColorF4(baseColorVector.X, baseColorVector.Y, baseColorVector.Z, baseColorVector.W)
            : ColorF4.White;

        XRTexture2D? mainTexture = ResolveGenericTexture(document, resolver, warnings, "_BaseMap", "_MainTex");

        XRMaterial material = mainTexture is not null
            ? XRMaterial.CreateLitTextureMaterial(mainTexture)
            : XRMaterial.CreateLitColorMaterial(baseColor);

        material.Name = document.Name;
        return material;
    }

    private static void ApplyPoiyomiScalarAndColorProperties(XRMaterial material, UnityMaterialDocument document)
    {
        MapVector4(document, material, "_Color", "_Color");
        MapInt(document, material, "_ColorThemeIndex", "_ColorThemeIndex");
        MapFloat(document, material, "_BumpScale", "_BumpScale");

        MapInt(document, material, "_MainAlphaMaskMode", "_MainAlphaMaskMode");
        MapFloat(document, material, "_AlphaMaskBlendStrength", "_AlphaMaskBlendStrength");
        MapFloat(document, material, "_AlphaMaskValue", "_AlphaMaskValue");
        MapFloat(document, material, "_AlphaMaskInvert", "_AlphaMaskInvert");
        MapFloat(document, material, "_AlphaMod", "_AlphaMod");

        MapFloat(document, material, "_Saturation", "_Saturation");
        MapFloat(document, material, "_MainBrightness", "_MainBrightness");
        MapFloat(document, material, "_MainHueShiftToggle", "_MainHueShiftToggle", "_MainHueShiftEnabled");
        MapFloat(document, material, "_MainHueShift", "_MainHueShift");
        MapFloat(document, material, "_MainHueShiftSpeed", "_MainHueShiftSpeed");
        MapInt(document, material, "_MainHueShiftColorSpace", "_MainHueShiftColorSpace");
        MapFloat(document, material, "_MainHueShiftReplace", "_MainHueShiftReplace");

        MapInt(document, material, "_LightingMode", "_LightingMode");
        MapInt(document, material, "_LightingColorMode", "_LightingColorMode");
        MapInt(document, material, "_LightingMapMode", "_LightingMapMode");
        MapInt(document, material, "_LightingDirectionMode", "_LightingDirectionMode");
        MapFloat(document, material, "_LightingCapEnabled", "_LightingCapEnabled");
        MapFloat(document, material, "_LightingCap", "_LightingCap");
        MapFloat(document, material, "_LightingMinLightBrightness", "_LightingMinLightBrightness");
        MapFloat(document, material, "_LightingMonochromatic", "_LightingMonochromatic");
        MapFloat(document, material, "_LightingIndirectUsesNormals", "_LightingIndirectUsesNormals");
        MapVector3(document, material, "_LightingShadowColor", "_LightingShadowColor");
        MapFloat(document, material, "_ShadowStrength", "_ShadowStrength", "_ShadowMainStrength");
        MapFloat(document, material, "_LightingIgnoreAmbientColor", "_LightingIgnoreAmbientColor");
        MapFloat(document, material, "_ShadowOffset", "_ShadowOffset");
        MapFloat(document, material, "_ForceFlatRampedLightmap", "_ForceFlatRampedLightmap");
        MapVector4(document, material, "_ShadowColor", "_ShadowColor", "_1st_ShadeColor");
        MapFloat(document, material, "_ShadowBorder", "_ShadowBorder", "_ShadowFlatBorder");
        MapFloat(document, material, "_ShadowBlur", "_ShadowBlur", "_ShadowFlatBlur");
        MapFloat(document, material, "_LightingWrappedWrap", "_LightingWrappedWrap");
        MapFloat(document, material, "_LightingWrappedNormalization", "_LightingWrappedNormalization");
        MapFloat(document, material, "_LightingGradientStart", "_LightingGradientStart");
        MapFloat(document, material, "_LightingGradientEnd", "_LightingGradientEnd");

        MapFloat(document, material, "_LightDataAOStrengthR", "_LightDataAOStrengthR");
        MapFloat(document, material, "_LightingShadowMaskStrengthR", "_LightingShadowMaskStrengthR");

        MapVector4(document, material, "_EmissionColor", "_EmissionColor");
        MapFloat(document, material, "_EmissionStrength", "_EmissionStrength");
        MapVector2(document, material, "_EmissionScrollingSpeed", "_EmissionScrollingSpeed", "_EmissionMapPan");
        MapFloat(document, material, "_EmissionScrollingVertexColor", "_EmissionScrollingVertexColor");

        MapVector4(document, material, "_MatcapColor", "_MatcapColor");
        MapFloat(document, material, "_MatcapIntensity", "_MatcapIntensity");
        MapFloat(document, material, "_MatcapBorder", "_MatcapBorder");
        MapInt(document, material, "_MatcapUVMode", "_MatcapUVMode");
        MapFloat(document, material, "_MatcapReplace", "_MatcapReplace");
        MapFloat(document, material, "_MatcapMultiply", "_MatcapMultiply");
        MapFloat(document, material, "_MatcapAdd", "_MatcapAdd");
        MapFloat(document, material, "_MatcapEmissionStrength", "_MatcapEmissionStrength");
        MapFloat(document, material, "_MatcapLightMask", "_MatcapLightMask");
        MapFloat(document, material, "_MatcapNormal", "_MatcapNormal");
        MapInt(document, material, "_MatcapMaskChannel", "_MatcapMaskChannel");
        MapFloat(document, material, "_MatcapMaskInvert", "_MatcapMaskInvert");

        MapVector4(document, material, "_RimLightColor", "_RimLightColor", "_RimColor");
        MapFloat(document, material, "_RimWidth", "_RimWidth");
        MapFloat(document, material, "_RimSharpness", "_RimSharpness");
        MapFloat(document, material, "_RimLightColorBias", "_RimLightColorBias", "_RimBiasIntensity");
        MapFloat(document, material, "_RimEmission", "_RimEmission");
        MapFloat(document, material, "_RimHideInShadow", "_RimHideInShadow");
        MapInt(document, material, "_RimStyle", "_RimStyle");
        MapFloat(document, material, "_RimBlendStrength", "_RimBlendStrength", "_RimStrength", "_RimMainStrength");
        MapInt(document, material, "_RimBlendMode", "_RimBlendMode", "_RimPoiBlendMode");
        MapInt(document, material, "_RimMaskChannel", "_RimMaskChannel");

        MapFloat(document, material, "_SpecularSmoothness", "_SpecularSmoothness", "_Smoothness");
        MapFloat(document, material, "_SpecularStrength", "_SpecularStrength", "_ApplySpecular");
        MapVector4(document, material, "_SpecularTint", "_SpecularTint");
        MapInt(document, material, "_SpecularType", "_SpecularType", "_SpecularToon");

        MapVector3(document, material, "_DetailTint", "_DetailTint");
        MapFloat(document, material, "_DetailTexIntensity", "_DetailTexIntensity");
        MapFloat(document, material, "_DetailBrightness", "_DetailBrightness");
        MapFloat(document, material, "_DetailNormalMapScale", "_DetailNormalMapScale");

        MapFloat(document, material, "_MainVertexColoringEnabled", "_MainVertexColoringEnabled");
        MapFloat(document, material, "_MainVertexColoringLinearSpace", "_MainVertexColoringLinearSpace");
        MapFloat(document, material, "_MainVertexColoring", "_MainVertexColoring");
        MapFloat(document, material, "_MainUseVertexColorAlpha", "_MainUseVertexColorAlpha");

        MapVector4(document, material, "_BackFaceColor", "_BackFaceColor");
        MapFloat(document, material, "_BackFaceBlendMode", "_BackFaceBlendMode");
        MapFloat(document, material, "_BackFaceEmission", "_BackFaceEmission", "_BackFaceEmissionStrength");
        MapFloat(document, material, "_BackFaceAlpha", "_BackFaceAlpha", "_BackFaceReplaceAlpha");

        MapVector4(document, material, "_GlitterColor", "_GlitterColor");
        MapFloat(document, material, "_GlitterDensity", "_GlitterDensity");
        if (document.TryGetFloat("_GlitterFrequency", out float glitterFrequency))
            SetFloat(material, "_GlitterDensity", Math.Clamp(glitterFrequency / 100.0f, 0.0f, 10.0f));
        MapFloat(document, material, "_GlitterSize", "_GlitterSize");
        MapFloat(document, material, "_GlitterSpeed", "_GlitterSpeed");
        MapFloat(document, material, "_GlitterBrightness", "_GlitterBrightness");
        MapFloat(document, material, "_GlitterRainbow", "_GlitterRainbow", "_GlitterRandomColors");
        if (document.TryGetVector("_GlitterAngleRange", out Vector4 glitterAngleRange))
        {
            SetFloat(material, "_GlitterMinAngle", glitterAngleRange.X);
            SetFloat(material, "_GlitterMaxAngle", glitterAngleRange.Y);
        }

        MapFloat(document, material, "_FlipbookColumns", "_FlipbookColumns");
        MapFloat(document, material, "_FlipbookRows", "_FlipbookRows");
        MapFloat(document, material, "_FlipbookFrameRate", "_FlipbookFrameRate");
        MapFloat(document, material, "_FlipbookManualFrame", "_FlipbookManualFrame");
        MapFloat(document, material, "_FlipbookBlendMode", "_FlipbookBlendMode");
        MapFloat(document, material, "_FlipbookCrossfade", "_FlipbookCrossfade");

        MapVector4(document, material, "_SSSColor", "_SSSColor");
        MapFloat(document, material, "_SSSPower", "_SSSPower", "_SSSSpread");
        MapFloat(document, material, "_SSSDistortion", "_SSSDistortion");
        MapFloat(document, material, "_SSSScale", "_SSSScale", "_SSSStrength");
        MapFloat(document, material, "_SSSAmbient", "_SSSAmbient");

        MapFloat(document, material, "_DissolveType", "_DissolveType");
        MapFloat(document, material, "_DissolveProgress", "_DissolveProgress", "_DissolveAlpha", "_DissolveAlpha0");
        MapVector4(document, material, "_DissolveEdgeColor", "_DissolveEdgeColor");
        MapFloat(document, material, "_DissolveEdgeWidth", "_DissolveEdgeWidth");
        MapFloat(document, material, "_DissolveEdgeEmission", "_DissolveEdgeEmission", "_DissolveToEmissionStrength");
        MapFloat(document, material, "_DissolveNoiseStrength", "_DissolveNoiseStrength", "_DissolveDetailStrength");
        MapVector3(document, material, "_DissolveStartPoint", "_DissolveStartPoint");
        MapVector3(document, material, "_DissolveEndPoint", "_DissolveEndPoint");
        MapFloat(document, material, "_DissolveInvert", "_DissolveInvert", "_DissolveInvertNoise");
        MapFloat(document, material, "_DissolveCutoff", "_DissolveCutoff", "_DissolveAlpha", "_DissolveAlpha0");

        MapFloat(document, material, "_ParallaxMode", "_ParallaxMode", "_ParallaxInternalHeightmapMode");
        MapFloat(document, material, "_ParallaxStrength", "_ParallaxStrength", "_ParallaxInternalMaxDepth");
        MapFloat(document, material, "_ParallaxMinSamples", "_ParallaxMinSamples", "_ParallaxInternalIterations");
        MapFloat(document, material, "_ParallaxMaxSamples", "_ParallaxMaxSamples", "_ParallaxInternalIterations");
        MapFloat(document, material, "_ParallaxOffset", "_ParallaxOffset");
        MapFloat(document, material, "_ParallaxMapChannel", "_ParallaxMapChannel");
    }

    private static void ApplyLilToonScalarAndColorProperties(XRMaterial material, UnityMaterialDocument document)
    {
        MapVector4(document, material, "_Color", "_Color", "_BaseColor");
        MapFloat(document, material, "_BumpScale", "_BumpScale");

        MapInt(document, material, "_MainAlphaMaskMode", "_AlphaMaskMode");
        MapFloat(document, material, "_AlphaMaskBlendStrength", "_AlphaMaskScale");
        MapFloat(document, material, "_AlphaMaskValue", "_AlphaMaskValue");

        ApplyLilToonColorAdjustmentProperties(material, document);

        MapFloat(document, material, "_ShadowStrength", "_ShadowStrength");
        MapVector4(document, material, "_ShadowColor", "_ShadowColor");
        MapFloat(document, material, "_ShadowBorder", "_ShadowBorder");
        MapFloat(document, material, "_ShadowBlur", "_ShadowBlur");

        MapVector4(document, material, "_EmissionColor", "_EmissionColor");
        MapFloat(document, material, "_EmissionStrength", "_EmissionBlend", "_EmissionMainStrength");
        MapVector2(document, material, "_EmissionMapPan", "_EmissionMap_ScrollRotate");

        MapVector4(document, material, "_MatcapColor", "_MatCapColor");
        MapFloat(document, material, "_MatcapIntensity", "_MatCapBlend", "_MatCapMainStrength");
        MapFloat(document, material, "_MatcapLightMask", "_MatCapShadowMask");
        MapFloat(document, material, "_MatcapNormal", "_MatCapNormalStrength");

        MapVector4(document, material, "_RimLightColor", "_RimColor");
        MapFloat(document, material, "_RimWidth", "_RimBorder");
        MapFloat(document, material, "_RimSharpness", "_RimFresnelPower", "_RimBlur");
        MapFloat(document, material, "_RimBlendStrength", "_RimMainStrength");
        MapFloat(document, material, "_RimEmission", "_RimEnableLighting");

        MapFloat(document, material, "_SpecularSmoothness", "_Smoothness");
        MapFloat(document, material, "_SpecularStrength", "_SpecularStrength", "_ApplySpecular");
        MapVector4(document, material, "_SpecularTint", "_SpecularColor");

        MapVector3(document, material, "_DetailTint", "_Color2nd");
        MapFloat(document, material, "_DetailTexIntensity", "_Main2ndBlend");
        MapFloat(document, material, "_DetailNormalMapScale", "_Bump2ndScale");

        MapVector4(document, material, "_BackFaceColor", "_BackfaceColor", "_BacklightColor");
        MapFloat(document, material, "_BackFaceEmission", "_BacklightMainStrength");
        if (document.TryGetVector("_BackfaceColor", out Vector4 backfaceColor))
            SetFloat(material, "_BackFaceAlpha", backfaceColor.W);
        else if (document.TryGetVector("_BacklightColor", out Vector4 backlightColor))
            SetFloat(material, "_BackFaceAlpha", backlightColor.W);

        MapVector4(document, material, "_GlitterColor", "_GlitterColor");
        MapFloat(document, material, "_GlitterBrightness", "_GlitterMainStrength");
        MapFloat(document, material, "_GlitterDensity", "_GlitterDensity", "_GlitterScaleRandomize");
        MapFloat(document, material, "_GlitterSize", "_GlitterSize");
        MapFloat(document, material, "_GlitterSpeed", "_GlitterAnimationSpeed");

        MapVector4(document, material, "_SSSColor", "_BacklightColor");
        MapFloat(document, material, "_SSSPower", "_BacklightMainStrength");
        MapFloat(document, material, "_SSSScale", "_BacklightNormalStrength");

        MapVector4(document, material, "_DissolveEdgeColor", "_DissolveColor");
        MapFloat(document, material, "_DissolveNoiseStrength", "_DissolveNoiseStrength");
        if (document.TryGetVector("_DissolveParams", out Vector4 dissolveParams))
        {
            SetFloat(material, "_DissolveProgress", dissolveParams.X);
            SetFloat(material, "_DissolveEdgeWidth", dissolveParams.Y);
            SetFloat(material, "_DissolveCutoff", dissolveParams.Z);
        }
        if (document.TryGetVector("_DissolvePos", out Vector4 dissolvePos))
            SetVector3(material, "_DissolveStartPoint", new Vector3(dissolvePos.X, dissolvePos.Y, dissolvePos.Z));

        MapFloat(document, material, "_ParallaxStrength", "_Parallax");
        MapFloat(document, material, "_ParallaxOffset", "_ParallaxOffset");
    }

    private static void ApplyLilToonColorAdjustmentProperties(XRMaterial material, UnityMaterialDocument document)
    {
        if (TryGetLilToonHsvgAdjustments(document, out float saturation, out float brightness))
        {
            SetFloat(material, "_Saturation", saturation);
            SetFloat(material, "_MainBrightness", brightness);
        }

        if (document.TryGetFloat("_MainGradationStrength", out float gradationStrength) &&
            MathF.Abs(gradationStrength) > 0.0001f)
        {
            SetFloat(material, "_MainBrightness", gradationStrength);
        }
    }

    private static bool TryGetLilToonHsvgAdjustments(UnityMaterialDocument document, out float saturation, out float brightness)
    {
        saturation = 0.0f;
        brightness = 0.0f;
        if (!document.TryGetVector("_MainTexHSVG", out Vector4 hsvg))
            return false;

        saturation = hsvg.Y - 1.0f;
        brightness = hsvg.Z - 1.0f;
        return MathF.Abs(saturation) > 0.0001f ||
               MathF.Abs(brightness) > 0.0001f;
    }

    private static void ApplyPoiyomiFeatureState(
        XRMaterial material,
        UnityMaterialDocument document,
        string? alphaSource,
        string? colorAdjustSource,
        string? shadowColorSource,
        string? aoSource,
        string? shadowMaskSource,
        string? emissionSource,
        string? matcapSource,
        string? matcapMaskSource,
        string? rimMaskSource,
        string? specularSource,
        string? detailMaskSource,
        string? detailTexSource,
        string? detailNormalSource,
        string? outlineMaskSource,
        string? backFaceSource,
        string? glitterMaskSource,
        string? flipbookSource,
        string? sssSource,
        string? dissolveSource,
        string? parallaxSource)
    {
        bool stylizedEnabled = !document.TryGetFloat("_ShadingEnabled", out float shadingEnabled) || shadingEnabled > 0.5f;
        material.SetUberFeatureEnabled("stylized-shading", stylizedEnabled);

        material.SetUberFeatureEnabled("alpha-masks",
            alphaSource is not null ||
            document.TryGetPositive("_MainAlphaMaskMode") ||
            document.TryGetPositive("_AlphaMaskBlendStrength"));

        material.SetUberFeatureEnabled("color-adjustments",
            colorAdjustSource is not null ||
            document.TryGetPositive("_ColorGradingToggle") ||
            document.TryGetPositive("_MainHueShiftToggle"));

        material.SetUberFeatureEnabled("material-ao",
            aoSource is not null ||
            document.TryGetPositive("_LightDataAOStrengthR"));

        material.SetUberFeatureEnabled("shadow-masks",
            shadowMaskSource is not null ||
            document.TryGetPositive("_LightingShadowMaskStrengthR"));

        material.SetUberFeatureEnabled("emission",
            document.TryGetPositive("_EnableEmission") ||
            emissionSource is not null);

        material.SetUberFeatureEnabled("matcap",
            document.TryGetPositive("_MatcapEnable") ||
            matcapSource is not null ||
            matcapMaskSource is not null);

        material.SetUberFeatureEnabled("rim-lighting",
            document.TryGetPositive("_EnableRimLighting") ||
            rimMaskSource is not null);

        material.SetUberFeatureEnabled("advanced-specular",
            specularSource is not null ||
            document.TryGetPositive("_ApplySpecular") ||
            document.TryGetPositive("_SpecularStrength"));

        material.SetUberFeatureEnabled("detail-textures",
            document.TryGetPositive("_DetailEnabled") ||
            detailMaskSource is not null ||
            detailTexSource is not null ||
            detailNormalSource is not null);

        material.SetUberFeatureEnabled("outline",
            document.TryGetPositive("_EnableOutlines") ||
            outlineMaskSource is not null);

        material.SetUberFeatureEnabled("backface",
            document.TryGetPositive("_BackFaceEnabled") ||
            backFaceSource is not null);

        material.SetUberFeatureEnabled("glitter",
            document.TryGetPositive("_GlitterEnable") ||
            glitterMaskSource is not null);

        material.SetUberFeatureEnabled("flipbook",
            document.TryGetPositive("_EnableFlipbook") ||
            flipbookSource is not null);

        material.SetUberFeatureEnabled("subsurface",
            document.TryGetPositive("_SubsurfaceScattering") ||
            sssSource is not null);

        material.SetUberFeatureEnabled("dissolve",
            document.TryGetPositive("_EnableDissolve") ||
            dissolveSource is not null);

        material.SetUberFeatureEnabled("parallax",
            parallaxSource is not null ||
            document.TryGetPositive("_ParallaxStrength") ||
            document.TryGetPositive("_ParallaxInternalMaxDepth"));

        // The current engine uber shader has no direct Poiyomi ramp sampler, but
        // a shadow color texture still means the stylized path is meaningful.
        if (shadowColorSource is not null)
            material.SetUberFeatureEnabled("stylized-shading", true);
    }

    private static void ApplyLilToonFeatureState(
        XRMaterial material,
        UnityMaterialDocument document,
        string? alphaSource,
        string? colorAdjustSource,
        string? shadowColorSource,
        string? aoSource,
        string? shadowMaskSource,
        string? emissionSource,
        string? matcapSource,
        string? matcapMaskSource,
        string? rimMaskSource,
        string? specularSource,
        string? detailMaskSource,
        string? detailTexSource,
        string? detailNormalSource,
        string? outlineMaskSource,
        string? backFaceSource,
        string? glitterMaskSource,
        string? flipbookSource,
        string? sssSource,
        string? dissolveSource,
        string? parallaxSource)
    {
        material.SetUberFeatureEnabled("stylized-shading",
            document.TryGetPositive("_UseShadow") ||
            shadowColorSource is not null ||
            shadowMaskSource is not null ||
            aoSource is not null);

        material.SetUberFeatureEnabled("alpha-masks",
            alphaSource is not null ||
            document.TryGetPositive("_AlphaMaskMode"));

        material.SetUberFeatureEnabled("color-adjustments",
            colorAdjustSource is not null ||
            TryGetLilToonHsvgAdjustments(document, out _, out _) ||
            document.TryGetPositive("_MainGradationStrength"));

        material.SetUberFeatureEnabled("material-ao",
            aoSource is not null ||
            document.TryGetPositive("_ShadowStrength"));

        material.SetUberFeatureEnabled("shadow-masks",
            shadowMaskSource is not null);

        material.SetUberFeatureEnabled("emission",
            document.TryGetPositive("_UseEmission") ||
            emissionSource is not null);

        material.SetUberFeatureEnabled("matcap",
            document.TryGetPositive("_UseMatCap") ||
            matcapSource is not null ||
            matcapMaskSource is not null);

        material.SetUberFeatureEnabled("rim-lighting",
            document.TryGetPositive("_UseRim") ||
            rimMaskSource is not null);

        material.SetUberFeatureEnabled("advanced-specular",
            specularSource is not null ||
            document.TryGetPositive("_ApplySpecular") ||
            document.TryGetPositive("_SpecularStrength"));

        material.SetUberFeatureEnabled("detail-textures",
            document.TryGetPositive("_UseMain2ndTex") ||
            detailMaskSource is not null ||
            detailTexSource is not null ||
            detailNormalSource is not null);

        material.SetUberFeatureEnabled("outline",
            document.TryGetPositive("_UseOutline") ||
            outlineMaskSource is not null);

        material.SetUberFeatureEnabled("backface",
            backFaceSource is not null ||
            document.HasAnyProperty("_BackfaceColor"));

        material.SetUberFeatureEnabled("glitter",
            document.TryGetPositive("_UseGlitter") ||
            glitterMaskSource is not null);

        material.SetUberFeatureEnabled("flipbook",
            flipbookSource is not null);

        material.SetUberFeatureEnabled("subsurface",
            document.TryGetPositive("_UseBacklight") ||
            sssSource is not null);

        material.SetUberFeatureEnabled("dissolve",
            dissolveSource is not null ||
            document.HasAnyProperty("_DissolveParams"));

        material.SetUberFeatureEnabled("parallax",
            document.TryGetPositive("_UseParallax") ||
            parallaxSource is not null);
    }

    private static void ApplyPoiyomiRenderState(XRMaterial material, UnityMaterialDocument document)
    {
        if (document.TryGetFloat("_Cull", out float cullMode))
        {
            material.RenderOptions.CullMode = (int)MathF.Round(cullMode) switch
            {
                0 => ECullMode.None,
                1 => ECullMode.Front,
                2 => ECullMode.Back,
                _ => material.RenderOptions.CullMode,
            };
        }

        float cutoff = document.TryGetFloat("_Cutoff", out float authoredCutoff)
            ? Math.Clamp(authoredCutoff, 0.0f, 1.0f)
            : material.AlphaCutoff;
        material.AlphaCutoff = cutoff;
        material.TransparencyMode = ResolvePoiyomiTransparencyMode(document);
    }

    private static void ApplyLilToonRenderState(XRMaterial material, UnityMaterialDocument document, string? shaderPath)
    {
        if (document.TryGetFloat("_Cull", out float cullMode))
        {
            material.RenderOptions.CullMode = (int)MathF.Round(cullMode) switch
            {
                0 => ECullMode.None,
                1 => ECullMode.Front,
                2 => ECullMode.Back,
                _ => material.RenderOptions.CullMode,
            };
        }

        float cutoff = document.TryGetFloat("_Cutoff", out float authoredCutoff)
            ? Math.Clamp(authoredCutoff, 0.0f, 1.0f)
            : material.AlphaCutoff;
        material.AlphaCutoff = cutoff;
        material.TransparencyMode = ResolveLilToonTransparencyMode(document, shaderPath);
    }

    private static ETransparencyMode ResolvePoiyomiTransparencyMode(UnityMaterialDocument document)
    {
        int mode = document.TryGetInt("_Mode", out int authoredMode)
            ? authoredMode
            : document.CustomRenderQueue >= 3000 ? 3 : 0;

        if (document.TryGetPositive("_AlphaToCoverage"))
            return ETransparencyMode.AlphaToCoverage;

        return mode switch
        {
            0 => ETransparencyMode.Opaque,
            1 => ETransparencyMode.Masked,
            9 => ETransparencyMode.Masked,
            4 => ETransparencyMode.Additive,
            2 or 3 or 5 or 6 or 7 => ETransparencyMode.WeightedBlendedOit,
            _ when document.TryGetFloat("_AlphaForceOpaque", out float forceOpaque) && forceOpaque <= 0.5f => ETransparencyMode.WeightedBlendedOit,
            _ => ETransparencyMode.Opaque,
        };
    }

    private static ETransparencyMode ResolveLilToonTransparencyMode(UnityMaterialDocument document, string? shaderPath)
    {
        if (document.TryGetPositive("_AlphaToMask"))
            return ETransparencyMode.AlphaToCoverage;

        int transparentMode = document.TryGetInt("_TransparentMode", out int authoredTransparentMode)
            ? authoredTransparentMode
            : -1;

        if (transparentMode is 1 or 5)
            return ETransparencyMode.Masked;

        if (transparentMode is 2 or 3 or 6)
            return ETransparencyMode.WeightedBlendedOit;

        string fileName = Path.GetFileName(shaderPath ?? string.Empty);
        if (fileName.Contains("cutout", StringComparison.OrdinalIgnoreCase))
            return ETransparencyMode.Masked;

        if (fileName.Contains("trans", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("ref", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("gem", StringComparison.OrdinalIgnoreCase))
        {
            return ETransparencyMode.WeightedBlendedOit;
        }

        if (document.CustomRenderQueue >= 3000)
            return ETransparencyMode.WeightedBlendedOit;

        if (document.CustomRenderQueue >= 2450 && document.CustomRenderQueue < 3000)
            return ETransparencyMode.Masked;

        return ETransparencyMode.Opaque;
    }

    private static string? AddMappedTexture(
        UnityMaterialDocument document,
        UnityAssetResolver resolver,
        List<string> warnings,
        List<XRTexture?> textures,
        string destinationSampler,
        string defaultSource,
        params string[] sourceAliases)
    {
        if (HasSampler(textures, destinationSampler))
            return null;

        foreach (string sourceName in EnumerateSourceNames(defaultSource, sourceAliases))
        {
            XRTexture2D? texture = ResolveUberTexture(document, resolver, warnings, sourceName, destinationSampler);
            if (texture is null)
                continue;

            textures.Add(texture);
            return sourceName;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSourceNames(string defaultSource, string[] aliases)
    {
        yield return defaultSource;
        foreach (string alias in aliases)
        {
            if (!string.Equals(alias, defaultSource, StringComparison.Ordinal))
                yield return alias;
        }
    }

    private static bool HasSampler(IEnumerable<XRTexture?> textures, string samplerName)
    {
        foreach (XRTexture? texture in textures)
        {
            if (string.Equals(texture?.SamplerName, samplerName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static XRTexture2D? ResolveUberTexture(
        UnityMaterialDocument document,
        UnityAssetResolver resolver,
        List<string> warnings,
        string sourceProperty,
        string samplerName)
    {
        if (!document.Textures.TryGetValue(sourceProperty, out UnityTextureProperty? textureProperty) ||
            textureProperty is null ||
            !textureProperty.TextureReference.HasExternalGuid)
        {
            return null;
        }

        string? texturePath = resolver.Resolve(textureProperty.TextureReference.Guid);
        if (string.IsNullOrWhiteSpace(texturePath) || !File.Exists(texturePath))
        {
            warnings.Add($"Could not resolve Unity texture '{sourceProperty}' ({textureProperty.TextureReference.Guid}) for material '{document.Name}'.");
            return null;
        }

        return ModelImporter.GetOrCreateUberSamplerTexture(texturePath, samplerName);
    }

    private static XRTexture2D? ResolveGenericTexture(
        UnityMaterialDocument document,
        UnityAssetResolver resolver,
        List<string> warnings,
        params string[] sourceProperties)
    {
        foreach (string sourceProperty in sourceProperties)
        {
            if (!document.Textures.TryGetValue(sourceProperty, out UnityTextureProperty? textureProperty) ||
                textureProperty is null ||
                !textureProperty.TextureReference.HasExternalGuid)
            {
                continue;
            }

            string? texturePath = resolver.Resolve(textureProperty.TextureReference.Guid);
            if (string.IsNullOrWhiteSpace(texturePath) || !File.Exists(texturePath))
            {
                warnings.Add($"Could not resolve Unity texture '{sourceProperty}' ({textureProperty.TextureReference.Guid}) for material '{document.Name}'.");
                continue;
            }

            try
            {
                XRTexture2D? loaded = Engine.Assets?.Load<XRTexture2D>(texturePath);
                if (loaded is not null)
                    return loaded;
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not load Unity texture '{texturePath}' through AssetManager. {ex.Message}");
            }

            return ModelImporter.GetOrCreateUberSamplerTexture(texturePath, sourceProperty);
        }

        return null;
    }

    private static XRTexture2D GetDefaultUberSamplerTexture(string samplerName, ColorF4 color)
        => DefaultUberSamplerTextures.GetOrAdd(samplerName, key => CreateDefaultUberSamplerTexture(key, color));

    private static bool IsDefaultSamplerTexture(XRTexture2D texture, string samplerName)
        => DefaultUberSamplerTextures.TryGetValue(samplerName, out XRTexture2D? defaultTexture) &&
           ReferenceEquals(texture, defaultTexture);

    private static XRTexture2D CreateDefaultUberSamplerTexture(string samplerName, ColorF4 color)
        => new(1u, 1u, color)
        {
            Name = samplerName,
            SamplerName = samplerName,
            MagFilter = ETexMagFilter.Linear,
            MinFilter = ETexMinFilter.Linear,
            UWrap = ETexWrapMode.Repeat,
            VWrap = ETexWrapMode.Repeat,
            AlphaAsTransparency = true,
            AutoGenerateMipmaps = false,
            Resizable = false,
        };

    private static void ApplyTextureTransform(XRMaterial material, UnityMaterialDocument document, string? sourceProperty, string destinationSampler)
    {
        if (string.IsNullOrWhiteSpace(sourceProperty))
            return;

        if (document.Textures.TryGetValue(sourceProperty, out UnityTextureProperty? textureProperty) && textureProperty is not null)
            SetVector4(material, $"{destinationSampler}_ST", new Vector4(textureProperty.Scale.X, textureProperty.Scale.Y, textureProperty.Offset.X, textureProperty.Offset.Y));

        if (document.TryGetVector($"{sourceProperty}Pan", out Vector4 pan))
            SetVector2(material, $"{destinationSampler}Pan", new Vector2(pan.X, pan.Y));

        if (document.TryGetInt($"{sourceProperty}UV", out int uv))
            SetInt(material, $"{destinationSampler}UV", uv);
    }

    private static bool IsPoiyomiToon93Material(UnityMaterialDocument document, UnityAssetResolver resolver, out string? shaderPath)
    {
        shaderPath = resolver.Resolve(document.Shader.Guid);
        string normalizedShaderPath = shaderPath?.Replace('\\', '/') ?? string.Empty;
        if (normalizedShaderPath.Contains("/_PoiyomiShaders/Shaders/9.3/Toon/", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(normalizedShaderPath).StartsWith("Poiyomi Toon", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(shaderPath) && File.Exists(shaderPath))
        {
            try
            {
                string shaderText = File.ReadAllText(shaderPath);
                if (shaderText.Contains("Shader \".poiyomi/Poiyomi Toon\"", StringComparison.Ordinal) &&
                    shaderText.Contains("Poiyomi 9.3", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        return document.HasAnyProperty("shader_master_label") &&
               document.HasAnyProperty("_ShadingEnabled") &&
               document.HasAnyProperty("_MainTex") &&
               (document.HasAnyProperty("_EnableEmission") ||
                document.HasAnyProperty("_MatcapEnable") ||
                document.HasAnyProperty("_EnableRimLighting"));
    }

    private static bool IsLilToonMaterial(UnityMaterialDocument document, UnityAssetResolver resolver, out string? shaderPath)
    {
        shaderPath = resolver.Resolve(document.Shader.Guid);
        string normalizedShaderPath = shaderPath?.Replace('\\', '/') ?? string.Empty;
        string shaderFileName = Path.GetFileName(normalizedShaderPath);
        if (normalizedShaderPath.Contains("/lilToon/Shader/", StringComparison.OrdinalIgnoreCase) &&
            shaderFileName.StartsWith("lts", StringComparison.OrdinalIgnoreCase) &&
            shaderFileName.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(shaderPath) && File.Exists(shaderPath))
        {
            try
            {
                string shaderText = File.ReadAllText(shaderPath);
                if (shaderText.Contains("Shader \"lilToon\"", StringComparison.Ordinal) ||
                    shaderText.Contains("Shader \"Hidden/lilToon", StringComparison.Ordinal) ||
                    shaderText.Contains("_lilToonVersion", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        return document.HasAnyProperty("_lilToonVersion") &&
               document.HasAnyProperty("_MainTex") &&
               (document.HasAnyProperty("_UseShadow") ||
                document.HasAnyProperty("_UseEmission") ||
                document.HasAnyProperty("_UseMatCap") ||
                document.HasAnyProperty("_UseRim"));
    }

    private static void MapFloat(UnityMaterialDocument document, XRMaterial material, string destination, params string[] sources)
    {
        foreach (string source in sources)
        {
            if (!document.TryGetFloat(source, out float value))
                continue;

            SetFloat(material, destination, value);
            return;
        }
    }

    private static void MapInt(UnityMaterialDocument document, XRMaterial material, string destination, params string[] sources)
    {
        foreach (string source in sources)
        {
            if (!document.TryGetInt(source, out int value))
                continue;

            SetInt(material, destination, value);
            return;
        }
    }

    private static void MapVector2(UnityMaterialDocument document, XRMaterial material, string destination, params string[] sources)
    {
        foreach (string source in sources)
        {
            if (!document.TryGetVector(source, out Vector4 value))
                continue;

            SetVector2(material, destination, new Vector2(value.X, value.Y));
            return;
        }
    }

    private static void MapVector3(UnityMaterialDocument document, XRMaterial material, string destination, params string[] sources)
    {
        foreach (string source in sources)
        {
            if (!document.TryGetVector(source, out Vector4 value))
                continue;

            SetVector3(material, destination, new Vector3(value.X, value.Y, value.Z));
            return;
        }
    }

    private static void MapVector4(UnityMaterialDocument document, XRMaterial material, string destination, params string[] sources)
    {
        foreach (string source in sources)
        {
            if (!document.TryGetVector(source, out Vector4 value))
                continue;

            SetVector4(material, destination, value);
            return;
        }
    }

    private static void SetFloat(XRMaterial material, string name, float value)
        => material.Parameter<ShaderFloat>(name)?.SetValue(value);

    private static void SetInt(XRMaterial material, string name, int value)
        => material.Parameter<ShaderInt>(name)?.SetValue(value);

    private static void SetVector2(XRMaterial material, string name, Vector2 value)
        => material.Parameter<ShaderVector2>(name)?.SetValue(value);

    private static void SetVector3(XRMaterial material, string name, Vector3 value)
        => material.Parameter<ShaderVector3>(name)?.SetValue(value);

    private static void SetVector4(XRMaterial material, string name, Vector4 value)
        => material.Parameter<ShaderVector4>(name)?.SetValue(value);

    private static UnityMaterialDocument ParseMaterialDocument(YamlMappingNode mapping, string materialPath)
    {
        var document = new UnityMaterialDocument
        {
            Name = GetScalarString(mapping, "m_Name") ?? Path.GetFileNameWithoutExtension(materialPath),
            Shader = ParseReference(GetNode(mapping, "m_Shader")),
            CustomRenderQueue = GetScalarInt(mapping, "m_CustomRenderQueue") ?? -1,
        };

        if (GetNode(mapping, "m_SavedProperties") is not YamlMappingNode savedProperties)
            return document;

        if (GetNode(savedProperties, "m_TexEnvs") is YamlSequenceNode texEnvs)
            ParseTextureProperties(texEnvs, document.Textures);

        if (GetNode(savedProperties, "m_Floats") is YamlSequenceNode floats)
            ParseFloatProperties(floats, document.Floats);

        if (GetNode(savedProperties, "m_Ints") is YamlSequenceNode ints)
            ParseIntProperties(ints, document.Ints);

        if (GetNode(savedProperties, "m_Colors") is YamlSequenceNode colors)
            ParseVectorProperties(colors, document.Vectors);

        return document;
    }

    private static void ParseTextureProperties(YamlSequenceNode sequence, Dictionary<string, UnityTextureProperty> destination)
    {
        foreach (YamlNode item in sequence.Children)
        {
            if (item is not YamlMappingNode entry)
                continue;

            foreach ((YamlNode keyNode, YamlNode valueNode) in entry.Children)
            {
                string? propertyName = (keyNode as YamlScalarNode)?.Value;
                if (string.IsNullOrWhiteSpace(propertyName) || valueNode is not YamlMappingNode textureNode)
                    continue;

                destination[propertyName] = new UnityTextureProperty(
                    ParseReference(GetNode(textureNode, "m_Texture")),
                    GetVector2(textureNode, "m_Scale", Vector2.One),
                    GetVector2(textureNode, "m_Offset", Vector2.Zero));
            }
        }
    }

    private static void ParseFloatProperties(YamlSequenceNode sequence, Dictionary<string, float> destination)
    {
        foreach (YamlNode item in sequence.Children)
        {
            if (item is not YamlMappingNode entry)
                continue;

            foreach ((YamlNode keyNode, YamlNode valueNode) in entry.Children)
            {
                string? propertyName = (keyNode as YamlScalarNode)?.Value;
                string? rawValue = (valueNode as YamlScalarNode)?.Value;
                if (string.IsNullOrWhiteSpace(propertyName) ||
                    !float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                {
                    continue;
                }

                destination[propertyName] = value;
            }
        }
    }

    private static void ParseIntProperties(YamlSequenceNode sequence, Dictionary<string, int> destination)
    {
        foreach (YamlNode item in sequence.Children)
        {
            if (item is not YamlMappingNode entry)
                continue;

            foreach ((YamlNode keyNode, YamlNode valueNode) in entry.Children)
            {
                string? propertyName = (keyNode as YamlScalarNode)?.Value;
                string? rawValue = (valueNode as YamlScalarNode)?.Value;
                if (string.IsNullOrWhiteSpace(propertyName) ||
                    !int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                {
                    continue;
                }

                destination[propertyName] = value;
            }
        }
    }

    private static void ParseVectorProperties(YamlSequenceNode sequence, Dictionary<string, Vector4> destination)
    {
        foreach (YamlNode item in sequence.Children)
        {
            if (item is not YamlMappingNode entry)
                continue;

            foreach ((YamlNode keyNode, YamlNode valueNode) in entry.Children)
            {
                string? propertyName = (keyNode as YamlScalarNode)?.Value;
                if (string.IsNullOrWhiteSpace(propertyName) || valueNode is not YamlMappingNode vectorNode)
                    continue;

                destination[propertyName] = GetVector4(vectorNode, Vector4.Zero);
            }
        }
    }

    private static YamlMappingNode? LoadUnityDocumentMapping(string assetPath, string documentType)
    {
        var yaml = new YamlStream();
        using var reader = new StreamReader(assetPath);
        yaml.Load(reader);

        foreach (YamlDocument document in yaml.Documents)
        {
            if (document.RootNode is not YamlMappingNode rootNode || rootNode.Children.Count == 0)
                continue;

            foreach ((YamlNode keyNode, YamlNode valueNode) in rootNode.Children)
            {
                string? key = (keyNode as YamlScalarNode)?.Value;
                if (string.Equals(key, documentType, StringComparison.Ordinal) && valueNode is YamlMappingNode mappingNode)
                    return mappingNode;
            }
        }

        return null;
    }

    private static YamlNode? GetNode(YamlMappingNode mapping, string key)
    {
        foreach ((YamlNode yamlKey, YamlNode yamlValue) in mapping.Children)
        {
            if (string.Equals((yamlKey as YamlScalarNode)?.Value, key, StringComparison.Ordinal))
                return yamlValue;
        }

        return null;
    }

    private static string? GetScalarString(YamlMappingNode mapping, string key)
        => (GetNode(mapping, key) as YamlScalarNode)?.Value;

    private static int? GetScalarInt(YamlMappingNode mapping, string key)
    {
        string? value = GetScalarString(mapping, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : null;
    }

    private static float? GetScalarFloat(YamlMappingNode mapping, string key)
    {
        string? value = GetScalarString(mapping, key);
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
            ? result
            : null;
    }

    private static Vector2 GetVector2(YamlMappingNode mapping, string key, Vector2 fallback)
    {
        if (GetNode(mapping, key) is not YamlMappingNode vectorMapping)
            return fallback;

        return new Vector2(
            GetScalarFloat(vectorMapping, "x") ?? fallback.X,
            GetScalarFloat(vectorMapping, "y") ?? fallback.Y);
    }

    private static Vector4 GetVector4(YamlMappingNode mapping, Vector4 fallback)
        => new(
            GetScalarFloat(mapping, "r") ?? GetScalarFloat(mapping, "x") ?? fallback.X,
            GetScalarFloat(mapping, "g") ?? GetScalarFloat(mapping, "y") ?? fallback.Y,
            GetScalarFloat(mapping, "b") ?? GetScalarFloat(mapping, "z") ?? fallback.Z,
            GetScalarFloat(mapping, "a") ?? GetScalarFloat(mapping, "w") ?? fallback.W);

    private static UnityReference ParseReference(YamlNode? node)
    {
        if (node is not YamlMappingNode mapping)
            return default;

        long fileId = 0;
        string? fileIdValue = GetScalarString(mapping, "fileID");
        if (!string.IsNullOrWhiteSpace(fileIdValue))
            long.TryParse(fileIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out fileId);

        int? type = null;
        string? typeValue = GetScalarString(mapping, "type");
        if (int.TryParse(typeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedType))
            type = parsedType;

        return new UnityReference(fileId, GetScalarString(mapping, "guid"), type);
    }

    private static string ResolveUnityProjectRoot(string sourcePath)
    {
        string normalizedPath = Path.GetFullPath(sourcePath);
        var current = new DirectoryInfo(Path.GetDirectoryName(normalizedPath) ?? normalizedPath);
        while (current is not null)
        {
            if (string.Equals(current.Name, "Assets", StringComparison.OrdinalIgnoreCase))
                return current.Parent?.FullName ?? current.FullName;

            current = current.Parent;
        }

        return Path.GetDirectoryName(normalizedPath) ?? normalizedPath;
    }

    private sealed class UnityMaterialDocument
    {
        public string Name { get; init; } = string.Empty;
        public UnityReference Shader { get; init; }
        public int CustomRenderQueue { get; init; } = -1;
        public Dictionary<string, UnityTextureProperty> Textures { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, float> Floats { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Ints { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Vector4> Vectors { get; } = new(StringComparer.Ordinal);

        public bool HasAnyProperty(string name)
            => Textures.ContainsKey(name) || Floats.ContainsKey(name) || Ints.ContainsKey(name) || Vectors.ContainsKey(name);

        public bool TryGetFloat(string name, out float value)
        {
            if (Floats.TryGetValue(name, out value))
                return true;

            if (Ints.TryGetValue(name, out int intValue))
            {
                value = intValue;
                return true;
            }

            value = 0.0f;
            return false;
        }

        public bool TryGetInt(string name, out int value)
        {
            if (Ints.TryGetValue(name, out value))
                return true;

            if (Floats.TryGetValue(name, out float floatValue))
            {
                value = (int)MathF.Round(floatValue);
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryGetVector(string name, out Vector4 value)
            => Vectors.TryGetValue(name, out value);

        public bool TryGetPositive(string name)
            => TryGetFloat(name, out float value) && value > 0.0001f;
    }

    private sealed record UnityTextureProperty(UnityReference TextureReference, Vector2 Scale, Vector2 Offset);

    private readonly record struct UnityReference(long FileId, string? Guid, int? Type)
    {
        public bool HasExternalGuid => !string.IsNullOrWhiteSpace(Guid);
    }

    private sealed class UnityAssetResolver(string projectRoot)
    {
        private readonly string _projectRoot = Path.GetFullPath(projectRoot);
        private readonly Dictionary<string, string> _assetPathsByGuid = new(StringComparer.OrdinalIgnoreCase);
        private bool _indexInitialized;

        public string? Resolve(string? guid)
        {
            if (string.IsNullOrWhiteSpace(guid))
                return null;

            EnsureGuidIndex();
            return _assetPathsByGuid.TryGetValue(guid, out string? path) ? path : null;
        }

        private void EnsureGuidIndex()
        {
            if (_indexInitialized)
                return;

            foreach (string root in EnumerateUnitySearchRoots())
            {
                foreach (string metaPath in Directory.EnumerateFiles(root, "*.meta", SearchOption.AllDirectories))
                {
                    string? guid = TryReadGuid(metaPath);
                    if (string.IsNullOrWhiteSpace(guid))
                        continue;

                    string assetPath = metaPath[..^5];
                    if (File.Exists(assetPath))
                        _assetPathsByGuid.TryAdd(guid, assetPath);
                }
            }

            _indexInitialized = true;
        }

        private IEnumerable<string> EnumerateUnitySearchRoots()
        {
            string assetsRoot = Path.Combine(_projectRoot, "Assets");
            if (Directory.Exists(assetsRoot))
                yield return assetsRoot;

            string packagesRoot = Path.Combine(_projectRoot, "Packages");
            if (Directory.Exists(packagesRoot))
                yield return packagesRoot;

            if (!Directory.Exists(assetsRoot) && !Directory.Exists(packagesRoot) && Directory.Exists(_projectRoot))
                yield return _projectRoot;
        }

        private static string? TryReadGuid(string metaPath)
        {
            foreach (string line in File.ReadLines(metaPath))
            {
                const string prefix = "guid: ";
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                    return line[prefix.Length..].Trim();
            }

            return null;
        }
    }
}
