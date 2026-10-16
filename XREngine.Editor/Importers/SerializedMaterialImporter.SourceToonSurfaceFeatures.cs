using System.Numerics;
using XREngine.Imaging;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Scene.Importers.SourceToon;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    private static void ApplySourceToonSurfaceFeatures(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        List<string> warnings)
    {
        material.SetUberFeatureEnabled("surface-extensions", true);
        material.SetUberFeatureEnabled("global-masks-themes", HasMaskOrTheme(document));
        material.SetUberFeatureEnabled("advanced-stylized-lighting", true);
        material.SetUberFeatureEnabled("advanced-pbr", HasPbrFeatures(document));
        material.SetUberFeatureEnabled("layered-matcap-rim", HasMatcapOrRim(document));
        material.SetUberFeatureEnabled("layered-decals", HasAnyPositive(document, "_DecalEnabled", "_DecalEnabled1", "_DecalEnabled2", "_DecalEnabled3"));
        material.SetUberFeatureEnabled("layered-emission", HasAnyPositive(document, "_EnableEmission", "_EnableEmission1", "_EnableEmission2", "_EnableEmission3"));
        material.SetUberFeatureEnabled("texture-array-flipbook", document.TryGetPositive("_EnableFlipbook"));
        material.EnsureUberStateInitialized();

        // Poiyomi reserves _ColorMask for the render-target channel mask. It is
        // normally an integer (15), not a color-mask texture. Keep the native
        // texture extension inert unless a source shader actually authored a
        // texture with that name; otherwise its white fallback replaces albedo.
        SetFloat(material, "_ColorMaskEnabled", HasExternalTexture(document, "_ColorMask") ? 1.0f : 0.0f);

        BindExactSurfaceParameters(material, document);
        BindSourceToonGlitter(material, document);
        BindSourceToonDissolve(material, document, diagnostics);
        MapInt(document, material, "_RimLightColorThemeIndex", "_RimLightColorThemeIndex");
        BindCompositeSurfaceParameters(material, document);
        BindSourceRimParameters(material, document, diagnostics, warnings);
        BindSourceLightingParameters(material, document, diagnostics, warnings);
        BindSourceClearCoatParameters(material, document, diagnostics, warnings);
        BindSurfaceFeatureTextures(material, document, resolver, diagnostics, warnings);
        BindSourceToonNormalMetadata(material, document);
        BindSurfaceCubemap(material, document, resolver, diagnostics, warnings);
        WarnForDeferredSurfaceAdapters(document, warnings);
    }

    private static bool HasMaskOrTheme(SerializedMaterialDocument document)
    {
        if (HasExternalTexture(
            document,
            "_ColorMask",
            "_GlobalMaskTexture",
            "_GlobalMaskTexture1",
            "_GlobalMaskTexture2",
            "_GlobalMaskTexture3") ||
            document.TryGetPositive("_GlobalMaskTexturesEnable"))
        {
            return true;
        }

        return HasTheme(document);
    }

    private static bool HasTheme(SerializedMaterialDocument document)
    {
        foreach ((string name, float value) in document.Floats)
        {
            if (value > 0.0001f &&
                name.EndsWith("ThemeIndex", StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach ((string name, int value) in document.Ints)
        {
            if (value != 0 &&
                name.EndsWith("ThemeIndex", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasPbrFeatures(SerializedMaterialDocument document)
        => HasAnyPositive(
            document,
            "_MochieBRDF",
            "_AnisotropyEnabled",
            "_ClearCoatBRDF",
            "_ClearCoatEnabled",
            "_Specular2Enabled",
            "_EnableRimEnviro",
            "_BacklightEnabled") ||
           HasExternalTexture(document, "_MochieMetallicMaps") ||
           IsCubemapEnabled(document) && HasExternalTexture(document, "_CubeMap");

    private static bool IsCubemapEnabled(SerializedMaterialDocument document)
        => document.TryGetPositive("_CubeMapEnabled");

    private static bool HasMatcapOrRim(SerializedMaterialDocument document)
        => HasAnyPositive(
            document,
            "_MatcapEnable",
            "_Matcap2Enable",
            "_Matcap3Enable",
            "_Matcap4Enable",
            "_EnableRimLighting",
            "_EnableRim2Lighting",
            "_EnableDepthRimLighting");

    private static bool HasAnyPositive(SerializedMaterialDocument document, params string[] properties)
    {
        foreach (string property in properties)
        {
            if (document.TryGetPositive(property))
                return true;
        }

        return false;
    }

    private static bool HasExternalTexture(SerializedMaterialDocument document, params string[] properties)
    {
        foreach (string property in properties)
        {
            if (document.Textures.TryGetValue(property, out SerializedTexturePropertyDocument? texture) &&
                texture is not null &&
                texture.TextureReference.HasExternalGuid)
            {
                return true;
            }
        }

        return false;
    }

    private static void BindExactSurfaceParameters(XRMaterial material, SerializedMaterialDocument document)
    {
        if (!material.TryGetUberMaterialState(out _, out ShaderUiManifest manifest))
            return;

        foreach (ShaderUiProperty property in manifest.Properties)
        {
            if (property.IsSampler)
                continue;

            switch (material.Parameter<ShaderVar>(property.Name))
            {
                case ShaderInt value when document.TryGetInt(property.Name, out int authored):
                    value.SetValue(authored);
                    break;
                case ShaderFloat value when document.TryGetFloat(property.Name, out float authored):
                    value.SetValue(authored);
                    break;
                case ShaderVector2 value when document.TryGetVector(property.Name, out Vector4 authored):
                    value.SetValue(new Vector2(authored.X, authored.Y));
                    break;
                case ShaderVector3 value when document.TryGetVector(property.Name, out Vector4 authored):
                    value.SetValue(new Vector3(authored.X, authored.Y, authored.Z));
                    break;
                case ShaderVector4 value when document.TryGetVector(property.Name, out Vector4 authored):
                    value.SetValue(authored);
                    break;
            }
        }
    }

    private static void BindCompositeSurfaceParameters(XRMaterial material, SerializedMaterialDocument document)
    {
        BindGlobalThemeParameters(material, document);

        SetFloat(material, "_DepthAlphaEnabled", document.TryGetPositive("_DepthAlphaToggle") ? 1.0f : 0.0f);
        SetVector4(
            material,
            "_DepthAlphaParams",
            new Vector4(
                GetFloat(document, "_DepthAlphaMinValue"),
                GetFloat(document, "_DepthAlphaMaxValue", 1.0f),
                GetFloat(document, "_DepthAlphaMinDepth"),
                GetFloat(document, "_DepthAlphaMaxDepth", 1.0f)));

        SetFloat(
            material,
            "_CubeMapStrength",
            IsCubemapEnabled(document) ? GetFloat(document, "_CubeMapIntensity", 1.0f) : 0.0f);
        MapInt(document, material, "_CubeMapUvMode", "_CubeMapUVMode");
        MapInt(document, material, "_CubeMapBlendType", "_CubemapBlendType");
        MapFloat(document, material, "_CubeMapBlendAmount", "_CubeMapBlendAmount");
        MapFloat(document, material, "_CubeMapEmissionStrength", "_CubeMapEmissionStrength");
        MapFloat(document, material, "_CubeMapLightMask", "_CubeMapLightMask");
        SetFloat(
            material,
            "_CubeMapCoordinateZSign",
            IsCubemapEnabled(document) && HasExternalTexture(document, "_CubeMap") ? -1.0f : 1.0f);

        for (int slot = 0; slot < 4; ++slot)
        {
            string suffix = slot == 0 ? string.Empty : slot.ToString();
            string indexedPrefix = $"_Decal{slot}";
            SetVector4(
                material,
                $"_DecalBlendParams{slot}",
                new Vector4(
                    GetInt(document, $"_DecalBlendType{suffix}"),
                    document.TryGetPositive($"_DecalEnabled{suffix}")
                        ? GetFloat(document, $"_DecalBlendAlpha{suffix}", 1.0f)
                        : 0.0f,
                    GetFloat(document, $"_DecalAlphaIntensity{suffix}", 1.0f),
                    GetFloat(document, $"_DecalEmissionStrength{suffix}")));
            SetInt(material, $"_DecalUvMode{slot}", GetInt(document, $"_DecalTexture{suffix}UV"));
            SetVector4(
                material,
                $"_DecalSlotModifiers{slot}",
                new Vector4(
                    GetInt(document, $"{indexedPrefix}FaceMask"),
                    GetInt(document, $"{indexedPrefix}GlobalMask"),
                    GetInt(document, $"_DecalMirroredUVMode{suffix}"),
                    GetFloat(document, $"{indexedPrefix}Depth")));
            SetVector4(
                material,
                $"_DecalSlotFx{slot}",
                new Vector4(
                    GetFloat(document, $"_DecalHueShift{suffix}"),
                    GetFloat(document, $"_DecalHueShiftSpeed{suffix}"),
                    GetFloat(document, $"{indexedPrefix}ChannelSeparation"),
                    GetInt(document, $"_DecalColor{suffix}ThemeIndex")));

            string emissionSuffix = slot == 0 ? string.Empty : slot.ToString();
            bool emissionEnabled = document.TryGetPositive($"_EnableEmission{emissionSuffix}");
            SetVector4(
                material,
                $"_EmissionSlotParams{slot}",
                new Vector4(
                    emissionEnabled ? GetFloat(document, $"_EmissionStrength{emissionSuffix}") : 0.0f,
                    GetFloat(document, $"_EmissionBlinkingEnabled{emissionSuffix}"),
                    GetFloat(document, $"_EmissiveBlink_Velocity{emissionSuffix}"),
                    GetFloat(document, $"_EmissionHueShiftSpeed{emissionSuffix}")));
            MapVector4(document, material, $"_EmissionSlotColor{slot}", $"_EmissionColor{emissionSuffix}");
            MapVector2(document, material, $"_EmissionSlotPan{slot}", $"_EmissionMap{emissionSuffix}Pan");
            MapInt(document, material, $"_EmissionSlotUv{slot}", $"_EmissionMap{emissionSuffix}UV");
            SetVector4(
                material,
                $"_EmissionSlotModifiers{slot}",
                new Vector4(
                    GetFloat(document, $"_EmissionReplace{emissionSuffix}"),
                    GetFloat(document, $"_EmissionCenterOutEnabled{emissionSuffix}"),
                    GetInt(document, $"_EmissionMask{emissionSuffix}GlobalMask"),
                    GetInt(document, $"_EmissionColor{emissionSuffix}ThemeIndex")));
            SetVector4(
                material,
                $"_EmissionSlotSampling{slot}",
                new Vector4(
                    GetFloat(document, $"_EmissionBaseColorAsMap{emissionSuffix}"),
                    GetInt(document, $"_EmissionMask{emissionSuffix}Channel"),
                    GetFloat(document, $"_EmissionMaskInvert{emissionSuffix}"),
                    0.0f));

            string matcapPrefix = slot switch
            {
                0 => "_Matcap",
                1 => "_Matcap2",
                2 => "_Matcap3",
                _ => "_Matcap4",
            };
            bool matcapEnabled = document.TryGetPositive($"{matcapPrefix}Enable");
            if (matcapEnabled)
            {
                SetFloat(material, "_SourceMatcap", 1.0f);
                // A zero hue shift with zero speed is neutral in shift mode.
                if (document.TryGetPositive($"{matcapPrefix}HueShiftEnabled") &&
                    (GetFloat(document, $"{matcapPrefix}HueShift") != 0.0f ||
                     GetFloat(document, $"{matcapPrefix}HueShiftSpeed") != 0.0f ||
                     GetInt(document, $"{matcapPrefix}HueSelectOrShift", 1) != 1))
                    throw new SourceVisualImportException($"Source matcap hue conversion is not supported on '{document.Name}'.");
                foreach (string unsupported in new[] { "ApplyToAlphaEnabled", "AlphaOverride", "MaskGlobalMask", "TPSDepthEnabled" })
                {
                    if (document.TryGetPositive($"{matcapPrefix}{unsupported}"))
                        throw new SourceVisualImportException($"Source matcap option '{matcapPrefix}{unsupported}' is enabled on '{document.Name}'.");
                }
                if (Math.Abs(GetFloat(document, $"{matcapPrefix}Rotation")) > 0.0001f ||
                    document.TryGetPositive($"_Matcap{slot}CircleMaskEnabled") ||
                    document.TryGetPositive($"_Matcap{slot}CustomNormal") ||
                    document.TryGetPositive($"_Matcap{slot}ALEnabled"))
                    throw new SourceVisualImportException($"A source matcap rotation, circle mask, custom normal, or AudioLink option is enabled on '{document.Name}'.");
                string blendProperty = slot switch
                {
                    0 => "_MatCapBlendUV1",
                    1 => "_MatCap2ndBlendUV1",
                    2 => "_MatCap3rdBlendUV1",
                    _ => "_MatCap4thBlendUV1",
                };
                if (document.TryGetVector(blendProperty, out Vector4 blendUv) &&
                    (blendUv.X != 0.0f || blendUv.Y != 0.0f))
                    throw new SourceVisualImportException($"Source matcap UV blend '{blendProperty}' is enabled on '{document.Name}'.");
                int projection = GetInt(document, $"{matcapPrefix}UVMode", 1);
                if (projection < 0 || projection > 2)
                    throw new SourceVisualImportException($"Source matcap projection '{projection}' is enabled on '{document.Name}'.");
            }
            SetVector4(
                material,
                $"_MatcapSlotParams{slot}",
                new Vector4(
                    matcapEnabled ? GetFloat(document, $"{matcapPrefix}Intensity", 1.0f) : 0.0f,
                    GetFloat(document, $"{matcapPrefix}LightMask"),
                    GetFloat(document, $"{matcapPrefix}EmissionStrength"),
                    GetFloat(document, $"{matcapPrefix}Normal", 1.0f)));
            SetVector4(
                material,
                $"_MatcapSlotBlend{slot}",
                new Vector4(
                    GetFloat(document, $"{matcapPrefix}Replace"),
                    GetFloat(document, $"{matcapPrefix}Multiply"),
                    GetFloat(document, $"{matcapPrefix}Add"),
                    GetFloat(document, $"{matcapPrefix}Screen")));
            SetVector4(
                material,
                $"_MatcapSlotExtra{slot}",
                new Vector4(
                    GetFloat(document, $"{matcapPrefix}Mixed"),
                    GetFloat(document, $"{matcapPrefix}Border", 0.43f),
                    GetInt(document, $"{matcapPrefix}UVMode", 1),
                    GetFloat(document, $"{matcapPrefix}LightColorMix")));
            SetInt(material, $"_MatcapSlotTheme{slot}", GetInt(document, $"{matcapPrefix}ColorThemeIndex"));
            MapVector4(document, material, $"_MatcapSlotColor{slot}", $"{matcapPrefix}Color");
            SetVector4(material, $"_MatcapSlotSource{slot}", new Vector4(
                GetFloat(document, $"{matcapPrefix}Smoothness", 1.0f),
                GetFloat(document, $"{matcapPrefix}BaseColorMix"),
                GetFloat(document, $"{matcapPrefix}AddToLight"),
                GetFloat(document, $"{matcapPrefix}MaskSmoothnessApply")));
            SetVector4(material, $"_MatcapSlotMask{slot}", new Vector4(
                GetInt(document, $"{matcapPrefix}MaskChannel"),
                GetFloat(document, $"{matcapPrefix}MaskInvert"),
                GetInt(document, $"{matcapPrefix}MaskSmoothnessChannel", 3),
                GetInt(document, $"{matcapPrefix}MaskUV")));
        }

        MapVector4(document, material, "_Rim2Color", "_Rim2Color", "_Rim2LightColor");
        SetVector4(
            material,
            "_Rim2Params",
            new Vector4(
                GetFloat(document, "_Rim2Width"),
                GetFloat(document, "_Rim2Sharpness", 1.0f),
                GetFloat(document, "_Rim2HideInShadow"),
                GetFloat(document, "_Rim2Intensity", 1.0f)));
    }

    private static void BindSourceRimParameters(
        XRMaterial material,
        SerializedMaterialDocument document,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        List<string> warnings)
    {
        bool enabled = document.TryGetPositive("_EnableRimLighting");
        string? unsupportedOption = enabled ? GetUnsupportedSourceRimOption(document) : null;
        if (unsupportedOption is not null)
        {
            AddDiagnostic(diagnostics, warnings,
                MaterialConversionDiagnosticCodes.IntentionalNativeDifference,
                $"Source rim option '{unsupportedOption}' on '{document.Name}' uses the approximate Uber shading path.",
                unsupportedOption);
        }
        SetVector4(material, "_SourceRimSettings", new Vector4(
            enabled && unsupportedOption is null ? 1.0f : 0.0f,
            GetFloat(document, "_RimLightingInvert"),
            GetFloat(document, "_RimPower", 1.0f),
            GetFloat(document, "_Is_NormalMapToRimLight", 1.0f)));
        SetVector4(material, "_SourceRimColorSettings", new Vector4(
            GetFloat(document, "_RimBaseColorMix"),
            GetFloat(document, "_RimBrightness", 1.0f),
            GetInt(document, "_RimPoiBlendMode"),
            0.0f));
    }

    private static bool SupportsExactSourceRim(SerializedMaterialDocument document)
        => document.TryGetPositive("_EnableRimLighting") &&
           GetUnsupportedSourceRimOption(document) is null;

    private static string? GetUnsupportedSourceRimOption(SerializedMaterialDocument document)
    {
        if (GetInt(document, "_RimStyle") != 0)
            return "_RimStyle";
        foreach (string property in new[]
        {
            "_RimShadowToggle", "_RimGlobalMask", "_RimApplyAlpha",
            "_RimApplyGlobalMaskIndex", "_RimBiasIntensity", "_RimMaskInvert"
        })
        {
            if (document.TryGetPositive(property))
                return property;
        }
        if (document.TryGetPositive("_RimHueShiftEnabled") &&
            (MathF.Abs(GetFloat(document, "_RimHueShift")) > 0.0001f ||
             MathF.Abs(GetFloat(document, "_RimHueShiftSpeed")) > 0.0001f ||
             GetInt(document, "_RimHueSelectOrShift", 1) != 1))
            return "_RimHueShiftEnabled";
        foreach (string property in new[] { "_RimMaskPan", "_RimTexPan" })
        {
            if (document.TryGetVector(property, out Vector4 pan) &&
                (MathF.Abs(pan.X) > 0.0001f || MathF.Abs(pan.Y) > 0.0001f))
                return property;
        }
        return null;
    }

    private static void BindGlobalThemeParameters(XRMaterial material, SerializedMaterialDocument document)
    {
        for (int theme = 0; theme < 4; ++theme)
        {
            MapVector4(document, material, $"_GlobalThemeColor{theme}", $"_GlobalThemeColor{theme}");
            SetVector3(
                material,
                $"_GlobalThemeAdjust{theme}",
                new Vector3(
                    GetFloat(document, $"_GlobalThemeHue{theme}"),
                    GetFloat(document, $"_GlobalThemeSaturation{theme}"),
                    GetFloat(document, $"_GlobalThemeValue{theme}")));
        }
    }

    private static float GetFloat(SerializedMaterialDocument document, string property, float fallback = 0.0f)
        => document.TryGetFloat(property, out float value) ? value : fallback;

    private static int GetInt(SerializedMaterialDocument document, string property, int fallback = 0)
        => document.TryGetInt(property, out int value) ? value : fallback;

    private static void BindSurfaceFeatureTextures(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        List<string> warnings)
    {
        (string Destination, string[] Sources)[] bindings =
        [
            ("_MainTexDistortionMap", ["_MainTexDistortionMap"]),
            ("_MainTexDistortionMask", ["_MainTexDistortionMask"]),
            ("_BackFaceNormalMap", ["_BackFaceNormalMap"]),
            ("_ColorMaskTexture", ["_ColorMask"]),
            ("_GlobalMaskTexture0", ["_GlobalMaskTexture"]),
            ("_GlobalMaskTexture1", ["_GlobalMaskTexture1"]),
            ("_GlobalMaskTexture2", ["_GlobalMaskTexture2"]),
            ("_GlobalMaskTexture3", ["_GlobalMaskTexture3"]),
            ("_DetailShadowMap", ["_DetailShadowMap"]),
            ("_LightingSDFMap", ["_LightingSDFMap", "_SDFLightingTexture"]),
            ("_Specular2Map", ["_Specular2Map"]),
            ("_BacklightMask", ["_BacklightMask"]),
            ("_CubeMapMask", ["_CubeMapMask"]),
            ("_DecalMask", ["_DecalMask"]),
            ("_DecalTexture", ["_DecalTexture"]),
            ("_DecalTexture1", ["_DecalTexture1"]),
            ("_DecalTexture2", ["_DecalTexture2"]),
            ("_DecalTexture3", ["_DecalTexture3"]),
            ("_Matcap0Tex", ["_Matcap"]),
            ("_Matcap0Mask", ["_MatcapMask"]),
            ("_Matcap1Tex", ["_Matcap2"]),
            ("_Matcap1Mask", ["_Matcap2Mask"]),
            ("_Matcap2Tex", ["_Matcap3"]),
            ("_Matcap2Mask", ["_Matcap3Mask"]),
            ("_Matcap3Tex", ["_Matcap4"]),
            ("_Matcap3Mask", ["_Matcap4Mask"]),
            ("_Rim2Mask", ["_Rim2Mask"]),
            ("_Emission0Tex", ["_EmissionMap"]),
            ("_Emission0Mask", ["_EmissionMask"]),
            ("_Emission1Tex", ["_EmissionMap1"]),
            ("_Emission1Mask", ["_EmissionMask1"]),
            ("_Emission2Tex", ["_EmissionMap2"]),
            ("_Emission2Mask", ["_EmissionMask2"]),
            ("_Emission3Tex", ["_EmissionMap3"]),
            ("_Emission3Mask", ["_EmissionMask3"]),
            ("_FlipbookMask", ["_FlipbookMask"]),
        ];

        foreach ((string destination, string[] sources) in bindings)
        {
            foreach (string source in sources)
            {
                XRTexture2D? texture = ResolveUberTexture(document, resolver, warnings, source, destination);
                if (texture is null)
                    continue;

                ReplaceMaterialSampler(material, destination, texture);
                ApplyTextureTransform(material, document, source, destination);
                break;
            }
        }

        BindFlipbookArray(material, document, resolver, diagnostics, warnings);
    }

    /// <summary>
    /// Imports Unity's equirectangular cubemap source as a native cubemap. A
    /// two-dimensional fallback cannot be bound to a samplerCube and was the
    /// reason authored metal reflections silently sampled the black default.
    /// </summary>
    private static void BindSurfaceCubemap(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        List<string> warnings)
    {
        // Optimized Poiyomi shaders keep a default cubemap reference even when
        // the module is disabled. Treat the enable flag as authoritative so a
        // non-layout source used only as an inert default cannot fail the whole
        // material conversion or add a large unused native cubemap asset.
        if (!IsCubemapEnabled(document) ||
            !document.Textures.TryGetValue("_CubeMap", out SerializedTexturePropertyDocument? property) ||
            property is null ||
            !property.TextureReference.HasExternalGuid)
        {
            return;
        }

        string? texturePath = resolver.Resolve(property.TextureReference.Guid);
        if (string.IsNullOrWhiteSpace(texturePath) || !File.Exists(texturePath))
        {
            string message = $"Could not resolve Unity cubemap '_CubeMap' ({property.TextureReference.Guid}) for material '{document.Name}'.";
            if (resolver.ImportContext is not null)
                throw new SourceVisualImportException(message);
            warnings.Add(message);
            return;
        }

        ValidateRequiredSerializedTexture(texturePath, resolver.ImportContext);
        try
        {
            using RuntimeImage source = RuntimeImageCodecs.Require().Decode(File.ReadAllBytes(texturePath));
            var baseMipmap = new CubeMipmap();
            bool isEquirectangular = source.Width == source.Height * 2;
            bool converted = isEquirectangular
                ? baseMipmap.SetEquirectangularMap(source)
                : baseMipmap.SetCrossCubeMap(source);
            if (!converted)
                throw new InvalidDataException("Expected a 2:1 equirectangular image or a 4:3/3:4 cubemap cross.");

            SerializedTextureImportDocument? settings = SerializedTextureImportDocumentParser.ParseFile(texturePath);
            CubeMipmap[] mipmaps = CreateCubemapMipChain(
                baseMipmap,
                settings?.GenerateMipMaps is not false);
            var cubemap = new XRTextureCube(mipmaps)
            {
                Name = Path.GetFileNameWithoutExtension(texturePath),
                SamplerName = "_CubeMap",
                SizedInternalFormat = XRTexture2D.DeriveESizedInternalFormat(baseMipmap.Sides[0].InternalFormat),
                ImportedColorSpace = ETextureColorSpace.Linear,
                ImportedUsage = ETextureImportUsage.Color,
                MagFilter = settings?.FilterMode == 0 ? ETexMagFilter.Nearest : ETexMagFilter.Linear,
                MinFilter = settings?.GenerateMipMaps is false
                    ? ETexMinFilter.Linear
                    : ETexMinFilter.LinearMipmapLinear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                WWrap = ETexWrapMode.ClampToEdge,
                LodBias = settings?.MipBias ?? 0.0f,
                AutoGenerateMipmaps = false,
                Resizable = false,
            };

            ReplaceMaterialSampler(material, "_CubeMap", cubemap);
            resolver.ImportContext?.MarkOutcome(texturePath, SourceImportConversionOutcome.Converted);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            string message = $"Could not construct native cubemap '{texturePath}': {ex.Message}";
            if (resolver.ImportContext is not null)
                throw new SourceVisualImportException(message);

            warnings.Add(message);
            diagnostics.Add(new MaterialConversionDiagnostic(
                MaterialConversionDiagnosticCodes.UnsupportedTextureAsset,
                MaterialConversionDiagnosticSeverity.Warning,
                message,
                "_CubeMap"));
        }
    }

    private static CubeMipmap[] CreateCubemapMipChain(CubeMipmap baseMipmap, bool generateMipmaps)
    {
        if (!generateMipmaps || baseMipmap.Sides.Length == 0)
            return [baseMipmap];

        uint baseExtent = baseMipmap.Sides[0].Width;
        if (baseExtent <= 1)
            return [baseMipmap];

        List<CubeMipmap> mipmaps = [baseMipmap];
        for (uint extent = baseExtent >> 1; extent > 0; extent >>= 1)
        {
            Mipmap2D[] sides = new Mipmap2D[6];
            for (int side = 0; side < sides.Length; ++side)
            {
                using RuntimeImage image = baseMipmap.Sides[side].GetImage();
                using RuntimeImage resized = RuntimeImageCodecs.Require().Resize(image, extent, extent, RuntimeImageResizeMode.Standard);
                sides[side] = new Mipmap2D(resized);
            }

            mipmaps.Add(new CubeMipmap(
                sides[0], sides[1], sides[2], sides[3], sides[4], sides[5]));
        }

        return [.. mipmaps];
    }

    private static void BindFlipbookArray(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        List<string> warnings)
    {
        if (!document.Textures.TryGetValue("_FlipbookTexArray", out SerializedTexturePropertyDocument? property) ||
            !property.TextureReference.HasExternalGuid)
        {
            return;
        }

        string? texturePath = resolver.Resolve(property.TextureReference.Guid);
        if (string.IsNullOrWhiteSpace(texturePath) || !File.Exists(texturePath))
        {
            ReportMissingTexture(
                resolver,
                document,
                "_FlipbookTexArray",
                $"Could not resolve Unity texture array '_FlipbookTexArray' ({property.TextureReference.Guid}) for material '{document.Name}'.",
                warnings);
            return;
        }

        SerializedTextureImportDocument? settings = SerializedTextureImportDocumentParser.ParseFile(texturePath);
        try
        {
            XRTexture2DArray texture;
            if (Path.GetExtension(texturePath).Equals(".gif", StringComparison.OrdinalIgnoreCase))
            {
                texture = new XRTexture2DArray();
                texture.Load3rdParty(texturePath);
            }
            else if (settings is not null)
            {
                texture = XRTexture2DArray.LoadGrid(texturePath, settings.FlipbookRows, settings.FlipbookColumns);
            }
            else
            {
                throw new InvalidDataException("TextureImporter metadata is required to preserve native array-layer order.");
            }

            texture.Name = "_FlipbookTexArray";
            texture.SamplerName = "_FlipbookTexArray";
            if (settings is not null)
            {
                foreach (XRTexture2D frame in texture.Textures)
                    ApplySerializedTextureImportSettings(frame, settings, "_FlipbookTexArray");
            }
            ReplaceMaterialSampler(material, "_FlipbookTexArray", texture);
            ApplyTextureTransform(material, document, "_FlipbookTexArray", "_FlipbookTexArray");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            diagnostics.Add(new MaterialConversionDiagnostic(
                MaterialConversionDiagnosticCodes.UnsupportedTextureAsset,
                MaterialConversionDiagnosticSeverity.Warning,
                $"Could not construct native flipbook array '{texturePath}': {ex.Message}",
                "_FlipbookTexArray"));
        }
    }

    private static void ReplaceMaterialSampler(XRMaterial material, string samplerName, XRTexture replacement)
    {
        List<XRTexture?> textures = [];
        foreach (XRTexture? texture in material.Textures)
        {
            if (!string.Equals(texture?.SamplerName, samplerName, StringComparison.Ordinal))
                textures.Add(texture);
        }

        textures.Add(replacement);
        material.Textures = [.. textures];
    }

    private static void WarnForDeferredSurfaceAdapters(SerializedMaterialDocument document, List<string> warnings)
    {

        if (document.TryGetPositive("_TPSPenetratorEnabled") || document.TryGetPositive("_TPSReceiverEnabled"))
            warnings.Add("Poiyomi TPS decal hooks were preserved, but remain neutral until the TPS adapter phase is enabled.");
        if (HasAnyPositive(document, "_Decal0VideoEnabled", "_Decal1VideoEnabled", "_Decal2VideoEnabled", "_Decal3VideoEnabled"))
            warnings.Add("Poiyomi decal video hooks require an engine video-texture binding and were imported without an implicit static fallback.");
    }
}
