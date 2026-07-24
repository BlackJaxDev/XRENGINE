using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers.Poiyomi;

namespace XREngine.Scene.Importers;

public sealed class UnityMaterialImportResult
{
    public XRMaterial? Material { get; init; }
    public bool IsPoiyomiToon { get; init; }
    public bool IsLilToon { get; init; }
    public string? ShaderPath { get; init; }
    public UnityMaterialDocument? SourceDocument { get; init; }
    public UnityResolvedAsset? ShaderAsset { get; init; }
    public PoiyomiMaterialDescriptor? PoiyomiDescriptor { get; init; }
    public string[] Warnings { get; init; } = [];
    public MaterialConversionDiagnostic[] Diagnostics { get; init; } = [];
}

public static class UnityMaterialImporter
{
    private static readonly ConcurrentDictionary<string, XRTexture2D> DefaultUberSamplerTextures = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<XRMaterial, UnityMaterialDocument> PoiyomiSourceDocuments = new();

    public static XRMaterial? Import(string materialPath, string? projectRoot = null)
        => ImportWithReport(materialPath, projectRoot).Material;
    /// <summary>
    /// Validates the UV channels requested by an imported Poiyomi material against a mesh.
    /// Missing channels fall back to UV0 in generated vertex shaders.
    /// </summary>
    public static MaterialConversionDiagnostic[] ValidateMeshCompatibility(XRMaterial material, uint availableUvChannels)
        => PoiyomiSourceDocuments.TryGetValue(material, out UnityMaterialDocument? document)
            ? PoiyomiUvUsage.Validate(document, availableUvChannels)
            : [];


    public static UnityMaterialImportResult ImportWithReport(string materialPath, string? projectRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(materialPath);

        string normalizedPath = Path.GetFullPath(materialPath);
        List<string> warnings = [];
        List<MaterialConversionDiagnostic> diagnostics = [];

        UnityMaterialDocument document;
        try
        {
            document = UnityMaterialDocumentParser.ParseFile(normalizedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            warnings.Add($"Could not parse Unity material '{normalizedPath}'. {ex.Message}");
            return new UnityMaterialImportResult { Warnings = [.. warnings] };
        }

        var resolver = new UnityAssetResolver(projectRoot ?? ResolveUnityProjectRoot(normalizedPath));
        UnityResolvedAsset shaderAsset = resolver.Resolve(document.Shader);
        PoiyomiShaderMatchResult poiyomiMatch = MatchPoiyomiToon93Material(document, resolver, out string? shaderPath);
        diagnostics.AddRange(poiyomiMatch.Diagnostics);
        PoiyomiMaterialDescriptor? poiyomiDescriptor = poiyomiMatch.IsAccepted
            ? PoiyomiMaterialDescriptorFactory.Create(document, resolver, poiyomiMatch, diagnostics)
            : null;
        foreach (MaterialConversionDiagnostic diagnostic in diagnostics)
            warnings.Add(diagnostic.ToString());
        bool isPoiyomiToon = poiyomiMatch.IsAccepted;
        bool isLilToon = false;
        if (!isPoiyomiToon && !poiyomiMatch.IsPoiyomiFamily)
            isLilToon = IsLilToonMaterial(document, resolver, out shaderPath);

        try
        {
            XRMaterial material = isPoiyomiToon
                ? ConvertPoiyomiToUberMaterial(document, resolver, warnings, diagnostics)
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
                SourceDocument = document,
                ShaderAsset = shaderAsset,
                PoiyomiDescriptor = poiyomiDescriptor,
                Warnings = [.. warnings],
                Diagnostics = [.. diagnostics],
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
                SourceDocument = document,
                ShaderAsset = shaderAsset,
                PoiyomiDescriptor = poiyomiDescriptor,
                Warnings = [.. warnings],
                Diagnostics = [.. diagnostics],
            };
        }
    }

    private static XRMaterial ConvertPoiyomiToUberMaterial(
        UnityMaterialDocument document,
        UnityAssetResolver resolver,
        List<string> warnings,
        List<MaterialConversionDiagnostic> diagnostics)
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
        string? toonRampSource = AddMappedTexture(document, resolver, warnings, textures, "_ToonRamp", "_ToonRamp");
        string? firstShadeSource = AddMappedTexture(document, resolver, warnings, textures, "_FirstShadeMap", "_1st_ShadeMap");
        string? secondShadeSource = AddMappedTexture(document, resolver, warnings, textures, "_SecondShadeMap", "_2nd_ShadeMap");
        string? shadowColorSource = AddMappedTexture(document, resolver, warnings, textures, "_ShadowColorTex", "_ShadowColorTex");
        string? aoSource = AddMappedTexture(document, resolver, warnings, textures, "_LightingAOMaps", "_LightingAOMaps");
        string? shadowMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_LightingShadowMasks", "_LightingShadowMasks");
        string? emissionSource = AddMappedTexture(document, resolver, warnings, textures, "_EmissionMap", "_EmissionMap");
        string? matcapSource = AddMappedTexture(document, resolver, warnings, textures, "_Matcap", "_Matcap");
        string? matcapMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_MatcapMask", "_MatcapMask", "_MatcapMask");
        string? rimColorSource = AddMappedTexture(document, resolver, warnings, textures, "_RimColorTexture", "_RimColorTex", "_RimTex");
        string? rimMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_RimMask", "_RimMask");
        string? specularSource = AddMappedTexture(document, resolver, warnings, textures, "_SpecularMap", "_SpecularMap");
        string? metallicSource = AddMappedTexture(document, resolver, warnings, textures, "_PBRMetallicMaps", "_MochieMetallicMaps", "_RGBAMetallicMaps", "_MetallicGlossMap");
        string? smoothnessSource = AddMappedTexture(document, resolver, warnings, textures, "_PBRSmoothnessMaps", "_RGBASmoothnessMaps", "_SmoothnessTex", "_MochieMetallicMaps", "_MetallicGlossMap");
        string? detailMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_DetailMask", "_DetailMask");
        string? detailTexSource = AddMappedTexture(document, resolver, warnings, textures, "_DetailTex", "_DetailTex");
        string? detailNormalSource = AddMappedTexture(document, resolver, warnings, textures, "_DetailNormalMap", "_DetailNormalMap");
        string? outlineMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_OutlineMask", "_OutlineMask", "_OutlineMask", "_OutlineTexture");
        string? backFaceSource = AddMappedTexture(document, resolver, warnings, textures, "_BackFaceTexture", "_BackFaceTexture");
        string? glitterMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_GlitterMask", "_GlitterMask");
        string? flipbookSource = AddMappedTexture(document, resolver, warnings, textures, "_FlipbookTexture", "_FlipbookTexture", "_FlipbookTexture", "_Flipbook");
        string? sssSource = AddMappedTexture(document, resolver, warnings, textures, "_SSSThicknessMap", "_SSSThicknessMap");
        string? dissolveNoiseSource = AddMappedTexture(document, resolver, warnings, textures, "_DissolveNoiseTexture", "_DissolveNoiseTexture");
        string? dissolveDetailSource = AddMappedTexture(document, resolver, warnings, textures, "_DissolveDetailNoise", "_DissolveDetailNoise");
        string? dissolveMaskSource = AddMappedTexture(document, resolver, warnings, textures, "_DissolveMask", "_DissolveMask");
        string? dissolveGradientSource = AddMappedTexture(document, resolver, warnings, textures, "_DissolveEdgeGradient", "_DissolveEdgeGradient");
        string? dissolveEdgeSource = AddMappedTexture(document, resolver, warnings, textures, "_DissolveEdgeTexture", "_DissolveToTexture");
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
        ApplyPoiyomiTextureTransform(material, document, toonRampSource, "_ToonRamp", "_ToonRampUVSelector", diagnostics);
        ApplyPoiyomiTextureTransform(material, document, firstShadeSource, "_FirstShadeMap", null, diagnostics);
        ApplyPoiyomiTextureTransform(material, document, secondShadeSource, "_SecondShadeMap", null, diagnostics);
        ApplyTextureTransform(material, document, aoSource, "_LightingAOMaps");
        ApplyTextureTransform(material, document, shadowMaskSource, "_LightingShadowMasks");
        ApplyTextureTransform(material, document, emissionSource, "_EmissionMap");
        ApplyTextureTransform(material, document, matcapMaskSource, "_MatcapMask");
        ApplyPoiyomiTextureTransform(material, document, rimMaskSource, "_RimMask", null, diagnostics);
        ApplyPoiyomiTextureTransform(material, document, rimColorSource, "_RimColorTexture", null, diagnostics);
        ApplyTextureTransform(material, document, specularSource, "_SpecularMap");
        ApplyPoiyomiTextureTransform(material, document, metallicSource, "_PBRMetallicMaps", null, diagnostics);
        ApplyPoiyomiTextureTransform(material, document, smoothnessSource, "_PBRSmoothnessMaps", null, diagnostics);
        ApplyTextureTransform(material, document, detailMaskSource, "_DetailMask");
        ApplyTextureTransform(material, document, detailTexSource, "_DetailTex");
        ApplyTextureTransform(material, document, detailNormalSource, "_DetailNormalMap");
        ApplyTextureTransform(material, document, outlineMaskSource, "_OutlineMask");
        ApplyPoiyomiTextureTransform(material, document, dissolveNoiseSource, "_DissolveNoiseTexture", null, diagnostics);
        ApplyPoiyomiTextureTransform(material, document, dissolveDetailSource, "_DissolveDetailNoise", null, diagnostics);
        ApplyPoiyomiTextureTransform(material, document, dissolveMaskSource, "_DissolveMask", null, diagnostics);
        ApplyTextureTransform(material, document, dissolveGradientSource, "_DissolveEdgeGradient");
        ApplyPoiyomiTextureTransform(material, document, dissolveEdgeSource, "_DissolveEdgeTexture", null, diagnostics);
        ApplyTextureTransform(material, document, parallaxSource, "_ParallaxMap");

        ApplyPoiyomiScalarAndColorProperties(material, document, diagnostics);
        ApplyPoiyomiPackedPbrProperties(material, document, metallicSource, smoothnessSource, diagnostics);
        ApplyPoiyomiFeatureState(material, document,
            alphaSource,
            colorAdjustSource,
            shadowColorSource,
            toonRampSource,
            firstShadeSource,
            secondShadeSource,
            aoSource,
            shadowMaskSource,
            emissionSource,
            matcapSource,
            matcapMaskSource,
            rimMaskSource,
            specularSource,
            detailMaskSource,
            rimColorSource,
            metallicSource,
            smoothnessSource,
            detailTexSource,
            detailNormalSource,
            outlineMaskSource,
            backFaceSource,
            glitterMaskSource,
            flipbookSource,
            sssSource,
            dissolveNoiseSource,
            dissolveDetailSource,
            dissolveMaskSource,
            parallaxSource,
            diagnostics,
            warnings);

        ApplyPoiyomiRenderState(material, document, diagnostics);
        PoiyomiSourceDocuments.Remove(material);
        PoiyomiSourceDocuments.Add(material, document);
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

    private static void ApplyPoiyomiScalarAndColorProperties(
        XRMaterial material,
        UnityMaterialDocument document,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        MapVector4(document, material, "_Color", "_Color");
        SetInt(material, "_ColorThemeIndex", PoiyomiEnumMapper.Identity(document, "_ColorThemeIndex", 0, 0, 12, diagnostics));
        MapFloat(document, material, "_BumpScale", "_BumpScale");

        SetInt(material, "_MainAlphaMaskMode", PoiyomiEnumMapper.AlphaMaskMode(document, diagnostics));
        MapFloat(document, material, "_AlphaMaskBlendStrength", "_AlphaMaskBlendStrength");
        MapFloat(document, material, "_AlphaMaskValue", "_AlphaMaskValue");
        MapFloat(document, material, "_AlphaMaskInvert", "_AlphaMaskInvert");
        MapFloat(document, material, "_AlphaMod", "_AlphaMod");

        MapFloat(document, material, "_Saturation", "_Saturation");
        MapFloat(document, material, "_MainBrightness", "_MainBrightness");
        MapFloat(document, material, "_MainHueShiftToggle", "_MainHueShiftToggle", "_MainHueShiftEnabled");
        MapFloat(document, material, "_MainHueShift", "_MainHueShift");
        MapFloat(document, material, "_MainHueShiftSpeed", "_MainHueShiftSpeed");
        SetInt(material, "_MainHueShiftColorSpace", PoiyomiEnumMapper.Identity(document, "_MainHueShiftColorSpace", 0, 0, 1, diagnostics));
        MapFloat(document, material, "_MainHueShiftReplace", "_MainHueShiftReplace");

        SetInt(material, "_LightingMode", PoiyomiEnumMapper.LightingMode(document, diagnostics));
        SetInt(material, "_LightingColorMode", PoiyomiEnumMapper.Identity(document, "_LightingColorMode", 0, 0, 3, diagnostics));
        SetInt(material, "_LightingMapMode", PoiyomiEnumMapper.Identity(document, "_LightingMapMode", 0, 0, 3, diagnostics));
        SetInt(material, "_LightingDirectionMode", PoiyomiEnumMapper.Identity(document, "_LightingDirectionMode", 0, 0, 3, diagnostics));
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
        SetInt(material, "_MatcapUVMode", PoiyomiEnumMapper.MatcapUvMode(document, diagnostics));
        MapFloat(document, material, "_MatcapReplace", "_MatcapReplace");
        MapFloat(document, material, "_MatcapMultiply", "_MatcapMultiply");
        MapFloat(document, material, "_MatcapAdd", "_MatcapAdd");
        MapFloat(document, material, "_MatcapEmissionStrength", "_MatcapEmissionStrength");
        MapFloat(document, material, "_MatcapLightMask", "_MatcapLightMask");
        MapFloat(document, material, "_MatcapNormal", "_MatcapNormal");
        SetInt(material, "_MatcapMaskChannel", PoiyomiEnumMapper.TextureChannel(document, "_MatcapMaskChannel", 0, diagnostics));
        MapFloat(document, material, "_MatcapMaskInvert", "_MatcapMaskInvert");

        MapVector4(document, material, "_RimLightColor", "_RimLightColor", "_RimColor");
        MapFloat(document, material, "_RimWidth", "_RimWidth");
        MapFloat(document, material, "_RimSharpness", "_RimSharpness");
        MapFloat(document, material, "_RimLightColorBias", "_RimLightColorBias", "_RimBiasIntensity");
        MapFloat(document, material, "_RimEmission", "_RimEmission");
        MapFloat(document, material, "_RimHideInShadow", "_RimHideInShadow");
        SetInt(material, "_RimStyle", PoiyomiEnumMapper.RimStyle(document, diagnostics));
        MapFloat(document, material, "_RimBlendStrength", "_RimBlendStrength", "_RimStrength", "_RimMainStrength");
        SetInt(material, "_RimBlendMode", PoiyomiEnumMapper.RimBlendMode(document, diagnostics));
        SetInt(material, "_RimMaskChannel", PoiyomiEnumMapper.TextureChannel(document, "_RimMaskChannel", 0, diagnostics));

        MapFloat(document, material, "_SpecularSmoothness", "_SpecularSmoothness", "_Smoothness");
        MapFloat(document, material, "_SpecularStrength", "_SpecularStrength", "_ApplySpecular");
        MapVector4(document, material, "_SpecularTint", "_SpecularTint");
        SetInt(material, "_SpecularType", PoiyomiEnumMapper.SpecularType(document, diagnostics));

        MapVector3(document, material, "_DetailTint", "_DetailTint");
        MapFloat(document, material, "_DetailTexIntensity", "_DetailTexIntensity");
        MapFloat(document, material, "_DetailBrightness", "_DetailBrightness");
        MapFloat(document, material, "_DetailNormalMapScale", "_DetailNormalMapScale");

        MapFloat(document, material, "_MainVertexColoringEnabled", "_MainVertexColoringEnabled");
        MapFloat(document, material, "_MainVertexColoringLinearSpace", "_MainVertexColoringLinearSpace");
        MapFloat(document, material, "_MainVertexColoring", "_MainVertexColoring");
        MapFloat(document, material, "_MainUseVertexColorAlpha", "_MainUseVertexColorAlpha");

        MapVector4(document, material, "_BackFaceColor", "_BackFaceColor");
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
        MapFloat(document, material, "_FlipbookCrossfade", "_FlipbookCrossfade");

        MapVector4(document, material, "_SSSColor", "_SSSColor");
        MapFloat(document, material, "_SSSPower", "_SSSPower", "_SSSSpread");
        MapFloat(document, material, "_SSSDistortion", "_SSSDistortion");
        MapFloat(document, material, "_SSSScale", "_SSSScale", "_SSSStrength");
        MapFloat(document, material, "_SSSAmbient", "_SSSAmbient");

        SetFloat(material, "_DissolveType", PoiyomiEnumMapper.DissolveMode(document, diagnostics));
        MapFloat(document, material, "_DissolveProgress", "_DissolveProgress", "_DissolveAlpha", "_DissolveAlpha0");
        MapVector4(document, material, "_DissolveEdgeColor", "_DissolveEdgeColor");
        MapFloat(document, material, "_DissolveEdgeWidth", "_DissolveEdgeWidth");
        MapFloat(document, material, "_DissolveEdgeEmission", "_DissolveEdgeEmission", "_DissolveToEmissionStrength");
        MapFloat(document, material, "_DissolveNoiseStrength", "_DissolveNoiseStrength");
        MapFloat(document, material, "_DissolveDetailStrength", "_DissolveDetailStrength");
        MapFloat(document, material, "_DissolveMaskInvert", "_DissolveMaskInvert");
        MapVector3(document, material, "_DissolveStartPoint", "_DissolveStartPoint");
        MapVector3(document, material, "_DissolveEndPoint", "_DissolveEndPoint");
        MapFloat(document, material, "_DissolveInvert", "_DissolveInvert", "_DissolveInvertNoise");
        MapFloat(document, material, "_DissolveCutoff", "_DissolveCutoff", "_DissolveAlpha", "_DissolveAlpha0");

        SetFloat(material, "_ParallaxMode", PoiyomiEnumMapper.ParallaxMode(document, diagnostics));
        MapFloat(document, material, "_ParallaxStrength", "_ParallaxStrength", "_ParallaxInternalMaxDepth");
        MapFloat(document, material, "_ParallaxMinSamples", "_ParallaxMinSamples", "_ParallaxInternalIterations");
        MapFloat(document, material, "_ParallaxMaxSamples", "_ParallaxMaxSamples", "_ParallaxInternalIterations");
        MapFloat(document, material, "_ParallaxOffset", "_ParallaxOffset");
        SetFloat(material, "_ParallaxMapChannel", PoiyomiEnumMapper.TextureChannel(
            document,
            "_ParallaxInternalMapMaskChannel",
            0,
            diagnostics));
    }

    private static void ApplyPoiyomiPackedPbrProperties(
        XRMaterial material,
        UnityMaterialDocument document,
        string? metallicSource,
        string? smoothnessSource,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        float metallicMultiplier = document.TryGetFloat("_Metallic", out float authoredMetallic)
            ? authoredMetallic
            : metallicSource is null ? 0.0f : 1.0f;
        SetFloat(material, "_PBRMetallicMultiplier", Math.Clamp(metallicMultiplier, 0.0f, 1.0f));

        int metallicChannel = 0;
        bool metallicInvert = false;
        if (string.Equals(metallicSource, "_MochieMetallicMaps", StringComparison.Ordinal))
        {
            metallicChannel = PoiyomiEnumMapper.TextureChannel(
                document,
                "_MochieMetallicMapsMetallicChannel",
                0,
                diagnostics);
            metallicInvert = document.TryGetPositive("_MochieMetallicMapInvert");
        }
        else if (string.Equals(metallicSource, "_RGBAMetallicMaps", StringComparison.Ordinal))
        {
            metallicInvert = document.TryGetPositive("_RGBARedMetallicInvert");
        }

        SetInt(material, "_PBRMetallicMapsMetallicChannel", metallicChannel);
        SetFloat(material, "_PBRMetallicMapInvert", metallicInvert ? 1.0f : 0.0f);

        float smoothnessMultiplier = document.TryGetFloat("_Smoothness", out float authoredSmoothness)
            ? authoredSmoothness
            : document.TryGetFloat("_SpecularSmoothness", out float specularSmoothness)
                ? specularSmoothness
                : smoothnessSource is null ? 0.5f : 1.0f;
        SetFloat(material, "_PBRRoughnessMultiplier", Math.Clamp(smoothnessMultiplier, 0.0f, 1.0f));

        int smoothnessChannel = 0;
        bool smoothnessInvert = false;
        if (string.Equals(smoothnessSource, "_MetallicGlossMap", StringComparison.Ordinal))
        {
            smoothnessChannel = 3;
        }
        else if (string.Equals(smoothnessSource, "_MochieMetallicMaps", StringComparison.Ordinal))
        {
            smoothnessChannel = PoiyomiEnumMapper.TextureChannel(
                document,
                "_MochieMetallicMapsRoughnessChannel",
                1,
                diagnostics);
            smoothnessInvert = true;
        }
        else if (string.Equals(smoothnessSource, "_RGBASmoothnessMaps", StringComparison.Ordinal))
        {
            smoothnessInvert = document.TryGetPositive("_RGBARedSmoothnessInvert");
        }

        SetInt(material, "_PBRSmoothnessMapsChannel", smoothnessChannel);
        SetFloat(material, "_PBRSmoothnessMapInvert", smoothnessInvert ? 1.0f : 0.0f);
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
        string? toonRampSource,
        string? firstShadeSource,
        string? secondShadeSource,
        string? aoSource,
        string? shadowMaskSource,
        string? emissionSource,
        string? matcapSource,
        string? matcapMaskSource,
        string? rimMaskSource,
        string? specularSource,
        string? detailMaskSource,
        string? rimColorSource,
        string? metallicSource,
        string? smoothnessSource,
        string? detailTexSource,
        string? detailNormalSource,
        string? outlineMaskSource,
        string? backFaceSource,
        string? glitterMaskSource,
        string? flipbookSource,
        string? sssSource,
        string? dissolveNoiseSource,
        string? dissolveDetailSource,
        string? dissolveMaskSource,
        string? parallaxSource,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        ICollection<string> warnings)
    {
        bool hasNormalMap =
            document.Textures.TryGetValue("_BumpMap", out UnityTexturePropertyDocument? bumpMap) &&
            bumpMap.TextureReference.HasExternalGuid &&
            (!document.TryGetFloat("_BumpScale", out float bumpScale) || MathF.Abs(bumpScale) > 0.0001f);
        material.SetUberFeatureEnabled("normal-map", hasNormalMap);

        bool stylizedEvidence =
            toonRampSource is not null ||
            firstShadeSource is not null ||
            secondShadeSource is not null ||
            shadowColorSource is not null ||
            document.TryGetInt("_LightingMode", out int lightingMode) && lightingMode != 5;
        material.SetUberFeatureEnabled("stylized-shading",
            PoiyomiFeatureStateResolver.IsEnabled(
                document,
                stylizedEvidence,
                ["_ShadingEnabled"],
                ["VIGNETTE_MASKED"]));

        material.SetUberFeatureEnabled("alpha-masks",
            alphaSource is not null ||
            document.TryGetPositive("_MainAlphaMaskMode") ||
            document.TryGetPositive("_AlphaMaskBlendStrength"));

        material.SetUberFeatureEnabled("color-adjustments",
            PoiyomiFeatureStateResolver.IsEnabled(
                document,
                colorAdjustSource is not null || document.TryGetPositive("_MainHueShiftToggle"),
                ["_ColorGradingToggle"],
                ["COLOR_GRADING_HDR"]));

        material.SetUberFeatureEnabled("material-ao",
            aoSource is not null || document.TryGetPositive("_LightDataAOStrengthR"));
        material.SetUberFeatureEnabled("shadow-masks",
            shadowMaskSource is not null || document.TryGetPositive("_LightingShadowMaskStrengthR"));

        material.SetUberFeatureEnabled("emission",
            PoiyomiFeatureStateResolver.IsEnabled(
                document,
                emissionSource is not null,
                ["_EnableEmission"],
                ["_EMISSION"]));

        material.SetUberFeatureEnabled("matcap",
            PoiyomiFeatureStateResolver.IsEnabled(
                document,
                matcapSource is not null || matcapMaskSource is not null,
                ["_MatcapEnable"],
                ["POI_MATCAP0"]));

        material.SetUberFeatureEnabled("rim-lighting",
            PoiyomiFeatureStateResolver.IsEnabled(
                document,
                rimColorSource is not null || rimMaskSource is not null,
                ["_EnableRimLighting"],
                ["_GLOSSYREFLECTIONS_OFF"]));

        material.SetUberFeatureEnabled("advanced-specular",
            PoiyomiFeatureStateResolver.IsEnabled(
                document,
                specularSource is not null || metallicSource is not null || smoothnessSource is not null ||
                document.TryGetPositive("_ApplySpecular") || document.TryGetPositive("_SpecularStrength"),
                ["_MochieBRDF", "_StylizedSpecular"],
                ["MOCHIE_PBR", "POI_STYLIZED_StylizedSpecular"]));

        material.SetUberFeatureEnabled("detail-textures",
            PoiyomiFeatureStateResolver.IsEnabled(
                document,
                detailMaskSource is not null || detailTexSource is not null || detailNormalSource is not null,
                ["_DetailEnabled"],
                ["FINALPASS"]));

        material.SetUberFeatureEnabled("outline", false);
        material.SetUberFeatureEnabled("backface",
            PoiyomiFeatureStateResolver.IsEnabled(
                document,
                backFaceSource is not null,
                ["_BackFaceEnabled"],
                ["POI_BACKFACE"]));
        material.SetUberFeatureEnabled("glitter",
            PoiyomiFeatureStateResolver.IsEnabled(
                document,
                glitterMaskSource is not null,
                ["_GlitterEnable"],
                ["_SUNDISK_SIMPLE"]));
        material.SetUberFeatureEnabled("flipbook",
            PoiyomiFeatureStateResolver.IsEnabled(
                document,
                flipbookSource is not null,
                ["_EnableFlipbook"],
                ["_SUNDISK_HIGH_QUALITY"]));
        material.SetUberFeatureEnabled("subsurface",
            PoiyomiFeatureStateResolver.IsEnabled(
                document,
                sssSource is not null,
                ["_SubsurfaceScattering"],
                ["POI_SUBSURFACESCATTERING"]));
        material.SetUberFeatureEnabled("dissolve",
            PoiyomiFeatureStateResolver.IsEnabled(
                document,
                dissolveNoiseSource is not null || dissolveDetailSource is not null || dissolveMaskSource is not null,
                ["_EnableDissolve"],
                ["DISTORT"]));
        material.SetUberFeatureEnabled("parallax",
            PoiyomiFeatureStateResolver.IsEnabled(
                document,
                parallaxSource is not null ||
                document.TryGetPositive("_ParallaxStrength") ||
                document.TryGetPositive("_ParallaxInternalMaxDepth"),
                ["_Parallax"],
                ["POI_PARALLAX", "POI_INTERNALPARALLAX"]));

        ReportUnsupportedPoiyomiFeatures(document, outlineMaskSource, diagnostics, warnings);
    }

    private static void ReportUnsupportedPoiyomiFeatures(
        UnityMaterialDocument document,
        string? outlineMaskSource,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        ICollection<string> warnings)
    {
        if (document.TryGetPositive("_EnableOutlines") || outlineMaskSource is not null)
        {
            AddDiagnostic(
                diagnostics,
                warnings,
                MaterialConversionDiagnosticCodes.IntentionalNativeDifference,
                "Poiyomi outline authoring was preserved, but no outline pass was enabled because inverse-hull rendering is not implemented yet.",
                "_EnableOutlines");
        }

        ReportUnsupportedIntegration(document, "_LTCGIEnabled", "LTCGI", diagnostics, warnings);
        ReportUnsupportedIntegration(document, "_EnableMirrorOptions", "VRChat mirror visibility", diagnostics, warnings);
        ReportUnsupportedIntegration(document, "_MirrorTextureEnabled", "VRChat mirror textures", diagnostics, warnings);
        ReportUnsupportedIntegration(document, "_AlphaAudioLinkEnabled", "AudioLink", diagnostics, warnings);

        if (document.ValidKeywords.Contains("POI_AUDIOLINK"))
        {
            AddDiagnostic(
                diagnostics,
                warnings,
                MaterialConversionDiagnosticCodes.IntegrationUnavailable,
                "AudioLink state was preserved, but the XRENGINE AudioLink integration is unavailable.",
                "POI_AUDIOLINK");
        }
    }

    private static void ReportUnsupportedIntegration(
        UnityMaterialDocument document,
        string sourceProperty,
        string displayName,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        ICollection<string> warnings)
    {
        if (!document.TryGetPositive(sourceProperty))
            return;

        AddDiagnostic(
            diagnostics,
            warnings,
            MaterialConversionDiagnosticCodes.IntegrationUnavailable,
            $"{displayName} state was preserved, but its XRENGINE integration is unavailable.",
            sourceProperty);
    }

    private static void AddDiagnostic(
        ICollection<MaterialConversionDiagnostic> diagnostics,
        ICollection<string> warnings,
        string code,
        string message,
        string? sourceProperty)
    {
        var diagnostic = new MaterialConversionDiagnostic(
            code,
            MaterialConversionDiagnosticSeverity.Warning,
            message,
            sourceProperty);
        diagnostics.Add(diagnostic);
        warnings.Add(diagnostic.ToString());
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

    private static void ApplyPoiyomiRenderState(
        XRMaterial material,
        UnityMaterialDocument document,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        material.RenderOptions.CullMode = PoiyomiEnumMapper.CullMode(document, material.RenderOptions.CullMode, diagnostics);

        float cutoff = document.TryGetFloat("_Cutoff", out float authoredCutoff)
            ? Math.Clamp(authoredCutoff, 0.0f, 1.0f)
            : material.AlphaCutoff;
        material.AlphaCutoff = cutoff;
        material.TransparencyMode = PoiyomiEnumMapper.TransparencyMode(document, diagnostics);
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
        if (!document.Textures.TryGetValue(sourceProperty, out UnityTexturePropertyDocument? textureProperty) ||
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

        XRTexture2D texture = ModelImporter.GetOrCreateUberSamplerTexture(texturePath, samplerName);
        UnityTextureImportDocument? importSettings = UnityTextureImportDocumentParser.ParseFile(texturePath);
        if (importSettings is not null)
            ApplyUnityTextureImportSettings(texture, importSettings, samplerName);
        return texture;
    }

    private static XRTexture2D? ResolveGenericTexture(
        UnityMaterialDocument document,
        UnityAssetResolver resolver,
        List<string> warnings,
        params string[] sourceProperties)
    {
        foreach (string sourceProperty in sourceProperties)
        {
            if (!document.Textures.TryGetValue(sourceProperty, out UnityTexturePropertyDocument? textureProperty) ||
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

    private static void ApplyUnityTextureImportSettings(
        XRTexture2D texture,
        UnityTextureImportDocument settings,
        string samplerName)
    {
        texture.ImportedColorSpace = settings.IsSrgb
            ? ETextureColorSpace.Srgb
            : ETextureColorSpace.Linear;
        texture.ImportedUsage = ResolveImportedTextureUsage(settings, samplerName);
        texture.ImportedNormalMapFlipGreen = settings.FlipGreenChannel;
        texture.AlphaAsTransparency = settings.AlphaIsTransparency;
        texture.AutoGenerateMipmaps = settings.GenerateMipMaps;
        texture.LodBias = settings.MipBias;
        texture.MaxAnisotropy = Math.Max(1, settings.Anisotropy);
        texture.UWrap = MapUnityWrapMode(settings.WrapU);
        texture.VWrap = MapUnityWrapMode(settings.WrapV);

        texture.MagFilter = settings.FilterMode == 0
            ? ETexMagFilter.Nearest
            : ETexMagFilter.Linear;
        texture.MinFilter = settings.FilterMode switch
        {
            0 when settings.GenerateMipMaps => ETexMinFilter.NearestMipmapNearest,
            0 => ETexMinFilter.Nearest,
            2 when settings.GenerateMipMaps => ETexMinFilter.LinearMipmapLinear,
            1 when settings.GenerateMipMaps => ETexMinFilter.LinearMipmapNearest,
            _ => ETexMinFilter.Linear,
        };

        if (texture.SizedInternalFormat is ESizedInternalFormat.Rgb8 or ESizedInternalFormat.Srgb8)
        {
            texture.SizedInternalFormat = settings.IsSrgb
                ? ESizedInternalFormat.Srgb8
                : ESizedInternalFormat.Rgb8;
        }
        else if (texture.SizedInternalFormat is ESizedInternalFormat.Rgba8 or ESizedInternalFormat.Srgb8Alpha8)
        {
            texture.SizedInternalFormat = settings.IsSrgb
                ? ESizedInternalFormat.Srgb8Alpha8
                : ESizedInternalFormat.Rgba8;
        }
    }

    private static ETextureImportUsage ResolveImportedTextureUsage(
        UnityTextureImportDocument settings,
        string samplerName)
    {
        if (settings.IsNormalMap ||
            samplerName.Contains("Normal", StringComparison.OrdinalIgnoreCase) ||
            samplerName.Contains("Bump", StringComparison.OrdinalIgnoreCase))
        {
            return ETextureImportUsage.Normal;
        }

        if (samplerName.Contains("Mask", StringComparison.OrdinalIgnoreCase) ||
            samplerName.Contains("Metallic", StringComparison.OrdinalIgnoreCase) ||
            samplerName.Contains("Smoothness", StringComparison.OrdinalIgnoreCase) ||
            samplerName.Contains("Parallax", StringComparison.OrdinalIgnoreCase) ||
            samplerName.Contains("Noise", StringComparison.OrdinalIgnoreCase) ||
            samplerName.Contains("AO", StringComparison.OrdinalIgnoreCase))
        {
            return ETextureImportUsage.Data;
        }

        return ETextureImportUsage.Color;
    }

    private static ETexWrapMode MapUnityWrapMode(int wrapMode)
        => wrapMode switch
        {
            0 => ETexWrapMode.Repeat,
            1 => ETexWrapMode.ClampToEdge,
            2 or 3 => ETexWrapMode.MirroredRepeat,
            _ => ETexWrapMode.Repeat,
        };

    private static void ApplyPoiyomiTextureTransform(
        XRMaterial material,
        UnityMaterialDocument document,
        string? sourceProperty,
        string destinationSampler,
        string? sourceUvSelector,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        ApplyTextureTransform(material, document, sourceProperty, destinationSampler);
        if (string.IsNullOrWhiteSpace(sourceProperty))
            return;

        string selector = sourceUvSelector ?? $"{sourceProperty}UV";
        if (!document.HasAnyProperty(selector))
            return;

        int uvChannel = PoiyomiEnumMapper.UvChannel(document, selector, 0, diagnostics);
        SetInt(material, $"{destinationSampler}UV", uvChannel);
    }

    private static void ApplyTextureTransform(XRMaterial material, UnityMaterialDocument document, string? sourceProperty, string destinationSampler)
    {
        if (string.IsNullOrWhiteSpace(sourceProperty))
            return;

        if (document.Textures.TryGetValue(sourceProperty, out UnityTexturePropertyDocument? textureProperty) && textureProperty is not null)
            SetVector4(material, $"{destinationSampler}_ST", new Vector4(textureProperty.Scale.X, textureProperty.Scale.Y, textureProperty.Offset.X, textureProperty.Offset.Y));

        if (document.TryGetVector($"{sourceProperty}Pan", out Vector4 pan))
            SetVector2(material, $"{destinationSampler}Pan", new Vector2(pan.X, pan.Y));

        if (document.TryGetInt($"{sourceProperty}UV", out int uv))
            SetInt(material, $"{destinationSampler}UV", uv);
    }

    private static PoiyomiShaderMatchResult MatchPoiyomiToon93Material(
        UnityMaterialDocument document,
        UnityAssetResolver resolver,
        out string? shaderPath)
    {
        shaderPath = resolver.Resolve(document.Shader.Guid);
        string? shaderSource = null;
        if (!string.IsNullOrWhiteSpace(shaderPath) && File.Exists(shaderPath))
        {
            try
            {
                shaderSource = File.ReadAllText(shaderPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return PoiyomiShaderMatcher.Match(new PoiyomiShaderMatchInput
        {
            ShaderPath = shaderPath,
            ShaderGuid = document.Shader.Guid,
            ShaderSource = shaderSource,
            PropertyNames = document.GetPropertyNames(),
            OverrideTags = document.OverrideTags,
        });
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
}
