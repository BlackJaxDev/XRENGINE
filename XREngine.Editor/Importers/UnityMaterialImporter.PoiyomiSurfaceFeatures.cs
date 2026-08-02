using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using XREngine.Scene.Importers.Poiyomi;

namespace XREngine.Scene.Importers;

public static partial class UnityMaterialImporter
{
    private static void ApplyPoiyomiSurfaceFeatures(
        XRMaterial material,
        UnityMaterialDocument document,
        UnityAssetResolver resolver,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        List<string> warnings)
    {
        material.SetUberFeatureEnabled("poiyomi-surface", true);
        material.SetUberFeatureEnabled("poiyomi-masks-themes", HasMaskOrTheme(document));
        material.SetUberFeatureEnabled("poiyomi-lighting-parity", true);
        material.SetUberFeatureEnabled("poiyomi-pbr-parity", HasPbrFeatures(document));
        material.SetUberFeatureEnabled("poiyomi-matcap-rim-slots", HasMatcapOrRim(document));
        material.SetUberFeatureEnabled("poiyomi-decals", HasAnyPositive(document, "_DecalEnabled", "_DecalEnabled1", "_DecalEnabled2", "_DecalEnabled3"));
        material.SetUberFeatureEnabled("poiyomi-emission-slots", HasAnyPositive(document, "_EnableEmission", "_EnableEmission1", "_EnableEmission2", "_EnableEmission3"));
        material.SetUberFeatureEnabled("poiyomi-flipbook-array", document.TryGetPositive("_EnableFlipbook"));
        material.EnsureUberStateInitialized();

        BindExactSurfaceParameters(material, document);
        BindCompositeSurfaceParameters(material, document);
        BindSurfaceFeatureTextures(material, document, resolver, diagnostics, warnings);
        WarnForDeferredSurfaceAdapters(document, warnings);
    }

    private static bool HasMaskOrTheme(UnityMaterialDocument document)
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

    private static bool HasTheme(UnityMaterialDocument document)
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

    private static bool HasPbrFeatures(UnityMaterialDocument document)
        => HasAnyPositive(
            document,
            "_MochieBRDF",
            "_AnisotropyEnabled",
            "_ClearCoatEnabled",
            "_Specular2Enabled",
            "_EnableRimEnviro",
            "_BacklightEnabled") ||
           HasExternalTexture(document, "_CubeMap", "_MochieMetallicMaps");

    private static bool HasMatcapOrRim(UnityMaterialDocument document)
        => HasAnyPositive(
            document,
            "_MatcapEnable",
            "_Matcap2Enable",
            "_Matcap3Enable",
            "_Matcap4Enable",
            "_EnableRimLighting",
            "_EnableRim2Lighting",
            "_EnableDepthRimLighting");

    private static bool HasAnyPositive(UnityMaterialDocument document, params string[] properties)
    {
        foreach (string property in properties)
        {
            if (document.TryGetPositive(property))
                return true;
        }

        return false;
    }

    private static bool HasExternalTexture(UnityMaterialDocument document, params string[] properties)
    {
        foreach (string property in properties)
        {
            if (document.Textures.TryGetValue(property, out UnityTexturePropertyDocument? texture) &&
                texture is not null &&
                texture.TextureReference.HasExternalGuid)
            {
                return true;
            }
        }

        return false;
    }

    private static void BindExactSurfaceParameters(XRMaterial material, UnityMaterialDocument document)
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

    private static void BindCompositeSurfaceParameters(XRMaterial material, UnityMaterialDocument document)
    {
        BindGlobalThemeParameters(material, document);

        for (int slot = 0; slot < 4; ++slot)
        {
            string suffix = slot == 0 ? string.Empty : slot.ToString();
            string indexedPrefix = $"_Decal{slot}";
            SetVector4(
                material,
                $"_DecalBlendParams{slot}",
                new Vector4(
                    GetInt(document, $"_DecalBlendType{suffix}"),
                    GetFloat(document, $"_DecalBlendAlpha{suffix}", 1.0f),
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
            SetVector4(
                material,
                $"_EmissionSlotParams{slot}",
                new Vector4(
                    GetFloat(document, $"_EmissionStrength{emissionSuffix}"),
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

            string matcapPrefix = slot switch
            {
                0 => "_Matcap",
                1 => "_Matcap2",
                2 => "_Matcap3",
                _ => "_Matcap4",
            };
            SetVector4(
                material,
                $"_MatcapSlotParams{slot}",
                new Vector4(
                    GetFloat(document, $"{matcapPrefix}Intensity"),
                    ResolveMatcapBlendMode(document, matcapPrefix),
                    GetFloat(document, $"{matcapPrefix}LightMask"),
                    GetFloat(document, $"{matcapPrefix}EmissionStrength")));
            MapVector4(document, material, $"_MatcapSlotColor{slot}", $"{matcapPrefix}Color");
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

    private static void BindGlobalThemeParameters(XRMaterial material, UnityMaterialDocument document)
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

    private static int ResolveMatcapBlendMode(UnityMaterialDocument document, string prefix)
    {
        if (document.TryGetPositive(prefix + "Multiply"))
            return 2;
        if (document.TryGetPositive(prefix + "Screen"))
            return 6;
        if (document.TryGetPositive(prefix + "Add"))
            return 8;
        if (document.TryGetPositive(prefix + "Mixed"))
            return 20;
        return 0;
    }

    private static float GetFloat(UnityMaterialDocument document, string property, float fallback = 0.0f)
        => document.TryGetFloat(property, out float value) ? value : fallback;

    private static int GetInt(UnityMaterialDocument document, string property, int fallback = 0)
        => document.TryGetInt(property, out int value) ? value : fallback;

    private static void BindSurfaceFeatureTextures(
        XRMaterial material,
        UnityMaterialDocument document,
        UnityAssetResolver resolver,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        List<string> warnings)
    {
        (string Destination, string[] Sources)[] bindings =
        [
            ("_MainTexDistortionMap", ["_MainTexDistortionMap"]),
            ("_MainTexDistortionMask", ["_MainTexDistortionMask"]),
            ("_BackFaceNormalMap", ["_BackFaceNormalMap"]),
            ("_ColorMask", ["_ColorMask"]),
            ("_GlobalMaskTexture0", ["_GlobalMaskTexture"]),
            ("_GlobalMaskTexture1", ["_GlobalMaskTexture1"]),
            ("_GlobalMaskTexture2", ["_GlobalMaskTexture2"]),
            ("_GlobalMaskTexture3", ["_GlobalMaskTexture3"]),
            ("_DetailShadowMap", ["_DetailShadowMap"]),
            ("_LightingSDFMap", ["_LightingSDFMap", "_SDFLightingTexture"]),
            ("_Specular2Map", ["_Specular2Map"]),
            ("_BacklightMask", ["_BacklightMask"]),
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

    private static void BindFlipbookArray(
        XRMaterial material,
        UnityMaterialDocument document,
        UnityAssetResolver resolver,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        List<string> warnings)
    {
        if (!document.Textures.TryGetValue("_FlipbookTexArray", out UnityTexturePropertyDocument? property) ||
            !property.TextureReference.HasExternalGuid)
        {
            return;
        }

        string? texturePath = resolver.Resolve(property.TextureReference.Guid);
        if (string.IsNullOrWhiteSpace(texturePath) || !File.Exists(texturePath))
        {
            warnings.Add($"Could not resolve Unity texture array '_FlipbookTexArray' ({property.TextureReference.Guid}) for material '{document.Name}'.");
            return;
        }

        UnityTextureImportDocument? settings = UnityTextureImportDocumentParser.ParseFile(texturePath);
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
                    ApplyUnityTextureImportSettings(frame, settings, "_FlipbookTexArray");
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

    private static void WarnForDeferredSurfaceAdapters(UnityMaterialDocument document, List<string> warnings)
    {

        if (document.TryGetPositive("_TPSPenetratorEnabled") || document.TryGetPositive("_TPSReceiverEnabled"))
            warnings.Add("Poiyomi TPS decal hooks were preserved, but remain neutral until the TPS adapter phase is enabled.");
        if (HasAnyPositive(document, "_Decal0VideoEnabled", "_Decal1VideoEnabled", "_Decal2VideoEnabled", "_Decal3VideoEnabled"))
            warnings.Add("Poiyomi decal video hooks require an engine video-texture binding and were imported without an implicit static fallback.");
    }
}
