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
        List<string> warnings,
        SourceToonShaderVersion? sourceVersion)
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
        SetFloat(material, "_SourceLayeredEmission", 1.0f);

        // Poiyomi reserves _ColorMask for the render-target channel mask. It is
        // normally an integer (15), not a color-mask texture. Keep the native
        // texture extension inert unless a source shader actually authored a
        // texture with that name; otherwise its white fallback replaces albedo.
        SetFloat(material, "_ColorMaskEnabled", HasExternalTexture(document, "_ColorMask") ? 1.0f : 0.0f);
        SetInt(material, "_SourceCubeMap", IsCubemapEnabled(document) ? 1 : 0);

        BindExactSurfaceParameters(material, document);
        BindSourceToonGlitter(material, document, resolver, sourceVersion, diagnostics);
        BindSourceToonFlipbook(material, document);
        BindSourceToonDissolve(material, document, diagnostics);
        MapInt(document, material, "_RimLightColorThemeIndex", "_RimLightColorThemeIndex");
        BindCompositeSurfaceParameters(material, document, resolver, sourceVersion);
        BindSourceAnisotropy(material, document, resolver, warnings);
        BindSourceUtsSpecular(material, document);
        BindSourceRimParameters(material, document, diagnostics, warnings);
        BindSourceLightingParameters(material, document, diagnostics, warnings);
        BindSourceClearCoatParameters(material, document, diagnostics, warnings);
        BindSurfaceFeatureTextures(material, document, resolver, diagnostics, warnings, sourceVersion);
        BindSourceToonNormalMetadata(material, document);
        BindSourceMultilayerTextures(material, document, resolver, diagnostics, warnings);
        BindSurfaceCubemap(material, document, resolver, diagnostics, warnings);
        WarnForDeferredSurfaceAdapters(document, warnings);
    }

    private static bool HasMaskOrTheme(SerializedMaterialDocument document)
    {
        if (HasExternalTexture(
            document,
            "_ColorMask",
            "_GlobalMaskTexture0",
            "_GlobalMaskTexture",
            "_GlobalMaskTexture1",
            "_GlobalMaskTexture2",
            "_GlobalMaskTexture3") ||
            document.TryGetPositive("_GlobalMaskTexturesEnable") ||
            HasAnyPositive(
                document,
                "_Decal0ApplyGlobalMaskIndex",
                "_Decal1ApplyGlobalMaskIndex",
                "_Decal2ApplyGlobalMaskIndex",
                "_Decal3ApplyGlobalMaskIndex"))
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
            "_EnableAniso",
            "_AnisotropyEnabled",
            "_ClearCoatBRDF",
            "_ClearCoatEnabled",
            "_Specular2Enabled",
            "_EnableEnvironmentalRim",
            "_BacklightEnabled") ||
           IsSourceUtsSpecularActive(document) ||
           HasExternalTexture(document, "_MochieMetallicMaps") ||
           IsCubemapEnabled(document) && HasExternalTexture(document, "_CubeMap");

    private static bool IsCubemapEnabled(SerializedMaterialDocument document)
        => document.TryGetPositive("_CubeMapEnabled");

    private static bool IsSourceUtsSpecularActive(SerializedMaterialDocument document)
        => document.TryGetPositive("_StylizedSpecular") &&
           GetInt(document, "_StylizedReflectionMode") == 0;

    private static void BindSourceUtsSpecular(
        XRMaterial material,
        SerializedMaterialDocument document)
    {
        if (!IsSourceUtsSpecularActive(document))
        {
            SetVector4(material, "_SourceUtsSettings", Vector4.Zero);
            return;
        }

        float strength = GetFloat(document, "_StylizedSpecularStrength", 1.0f);
        float normalStrength = GetFloat(document, "_StylizedSpecularNormalStrength", 1.0f);
        float specularToHighColor = GetFloat(document, "_Is_SpecularToHighColor");
        float blendMode = GetInt(document, "_Is_BlendAddToHiColor");
        SetVector4(material, "_SourceUtsSettings", new Vector4(strength, normalStrength, specularToHighColor, blendMode));

        SetVector4(material, "_SourceUtsLayer1", new Vector4(
            GetFloat(document, "_HighColor_Power"),
            GetFloat(document, "_StylizedSpecularFeather"),
            GetFloat(document, "_Layer1Strength", 1.0f),
            GetFloat(document, "_UseLightColor", 1.0f)));

        SetVector4(material, "_SourceUtsLayer2", new Vector4(
            GetFloat(document, "_Layer2Size"),
            GetFloat(document, "_StylizedSpecular2Feather"),
            GetFloat(document, "_Layer2Strength"),
            GetFloat(document, "_Tweak_HighColorMaskLevel")));

        Vector4 highColor = document.TryGetVector("_HighColor", out Vector4 rawHighColor)
            ? rawHighColor
            : Vector4.One;
        float themeIndex = GetInt(document, "_HighColorThemeIndex");
        SetVector4(material, "_SourceUtsColor", new Vector4(highColor.X, highColor.Y, highColor.Z, themeIndex));

        SetVector4(material, "_SourceUtsFlags", new Vector4(
            GetFloat(document, "_StylizedSpecularIgnoreShadow"),
            GetFloat(document, "_StylizedSpecularIgnoreNormal"),
            GetFloat(document, "_SSIgnoreCastedShadows"),
            GetFloat(document, "_StylizedSpecularInvertMask")));
    }

    private static void BindSourceAnisotropy(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        List<string> warnings)
    {
        if (!document.TryGetPositive("_EnableAniso"))
            return;

        int colorMapUv = GetInt(document, "_AnisoColorMapUV");
        if (colorMapUv is < 0 or > 3)
            throw new SourceVisualImportException($"Source anisotropy color map UV '{colorMapUv}' is not supported on '{document.Name}'.");

        int globalMask = GetInt(document, "_AnisoGlobalMask");
        int globalBlend = GetInt(document, "_AnisoGlobalMaskBlendType", 2);
        bool globalMasksEnabled = document.TryGetPositive("_GlobalMaskTexturesEnable");
        bool hasGlobalMaskTexture = globalMask is >= 1 and <= 16 &&
            (HasExternalTexture(document, $"_GlobalMaskTexture{(globalMask - 1) / 4}") ||
                globalMask <= 4 && HasExternalTexture(document, "_GlobalMaskTexture"));
        if (globalMask != 0 && (globalMask is < 1 or > 16 ||
            globalBlend is not (0 or 2 or 5 or 6 or 7 or 8 or 9 or 20) ||
            globalMasksEnabled && !hasGlobalMaskTexture ||
            !globalMasksEnabled && globalBlend != 2))
            throw new SourceVisualImportException($"Source anisotropy global mask is not supported on '{document.Name}'.");
        if (!globalMasksEnabled && globalBlend == 2)
            globalMask = 0;
        if (globalMask == 0)
            globalBlend = -1;

        XRTexture2D? colorMap = ResolveUberTexture(document, resolver, warnings,
            "_AnisoColorMap", "_AnisoColorMap");
        if (colorMap is not null)
        {
            ReplaceMaterialSampler(material, "_AnisoColorMap", colorMap);
            ApplyTextureTransform(material, document, "_AnisoColorMap", "_AnisoColorMap");
        }
        SetInt(material, "_AnisoColorMapUV", colorMapUv);
        SetVector4(material, "_SourceAnisoMapSettings", new Vector4(
            colorMap is null ? 0.0f : 1.0f, globalMask, globalBlend, 0.0f));
        SetVector2(material, "_SourceAnisoOffsetMapStrength", new Vector2(
            GetFloat(document, "_Aniso0OffsetMapStrength"),
            GetFloat(document, "_Aniso1OffsetMapStrength")));

        SetVector4(material, "_SourceAnisoSettings", new Vector4(
            1.0f,
            GetFloat(document, "_AnisoAdd", 1.0f),
            GetFloat(document, "_AnisoReplace"),
            GetFloat(document, "_AnisoNormalBlend", 1.0f)));
        SetVector4(material, "_SourceAnisoMix", new Vector4(
            GetFloat(document, "_AnisoUseBaseColor"),
            GetFloat(document, "_AnisoUseLightColor", 1.0f),
            GetFloat(document, "_AnisoHideInShadow", 1.0f),
            GetFloat(document, "_AnisoFadeByLightDirection")));

        for (int layer = 0; layer < 2; ++layer)
        {
            string prefix = $"_Aniso{layer}";
            SetVector4(material, $"_SourceAnisoToon{layer}", new Vector4(
                GetFloat(document, $"{prefix}ToonMode"),
                GetFloat(document, $"{prefix}Edge", 0.5f),
                GetFloat(document, $"{prefix}Blur"),
                0.0f));
            SetVector4(material, $"_SourceAnisoLayer{layer}", new Vector4(
                GetFloat(document, $"{prefix}Strength", 1.0f),
                GetFloat(document, $"{prefix}Power"),
                GetFloat(document, $"{prefix}Offset"),
                GetFloat(document, $"{prefix}SwitchDirection")));
            Vector4 tint = document.TryGetVector($"{prefix}Tint", out Vector4 authoredTint)
                ? authoredTint
                : Vector4.One;
            SetVector4(material, $"_SourceAnisoTint{layer}", new Vector4(
                tint.X, tint.Y, tint.Z, GetInt(document, $"{prefix}TintIndex")));
        }
    }

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

    private static void BindCompositeSurfaceParameters(
        XRMaterial material,
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        SourceToonShaderVersion? sourceVersion)
    {
        BindGlobalThemeParameters(material, document);

        // Saved values can remain after the source module is disabled.
        SetFloat(material, "_RimEnviroIntensity", document.TryGetPositive("_EnableEnvironmentalRim")
            ? GetFloat(document, "_RimEnviroIntensity", 1.0f) : 0.0f);
        SetVector4(material, "_SourceEnviroRim", new Vector4(
            document.TryGetPositive("_EnableEnvironmentalRim") ? 1.0f : 0.0f,
            GetFloat(document, "_RimEnviroBlur", 0.7f),
            GetFloat(document, "_RimEnviroMinBrightness"),
            GetFloat(document, "_RimEnviroChannel")));
        MapInt(document, material, "_RimEnviroMaskUV", "_RimEnviroMaskUV");

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
        SetFloat(material, "_SourceGrabPassBlendMode", GetFloat(document, "_SourceGrabPassBlendMode"));
        SetFloat(material, "_SourceGrabPassBlendFactor", GetFloat(document, "_SourceGrabPassBlendFactor", 1.0f));

        for (int slot = 0; slot < 4; ++slot)
        {
            string suffix = slot == 0 ? string.Empty : slot.ToString();
            string indexedPrefix = $"_Decal{slot}";
            bool decalEnabled = document.TryGetPositive($"_DecalEnabled{suffix}");
            SetVector4(
                material,
                $"_DecalBlendParams{slot}",
                new Vector4(
                    GetInt(document, $"_DecalBlendType{suffix}"),
                    decalEnabled
                        ? GetFloat(document, $"_DecalBlendAlpha{suffix}", 1.0f)
                        : 0.0f,
                    GetInt(document, $"_DecalOverrideAlpha{suffix}"),
                    GetFloat(document, $"_DecalEmissionStrength{suffix}")));
            SetInt(material, $"_DecalUvMode{slot}", GetInt(document, $"_DecalTexture{suffix}UV"));
            bool globalMasksEnabled = document.TryGetPositive("_GlobalMaskTexturesEnable");
            int decalGlobalMask = globalMasksEnabled
                ? GetInt(document, $"{indexedPrefix}GlobalMask")
                : 0;
            int decalApplyGlobalMask = globalMasksEnabled
                ? GetInt(document, $"{indexedPrefix}ApplyGlobalMaskIndex")
                : 0;
            SetVector4(
                material,
                $"_DecalSlotModifiers{slot}",
                new Vector4(
                    GetInt(document, $"{indexedPrefix}FaceMask"),
                    decalGlobalMask,
                    GetInt(document, $"_DecalMirroredUVMode{suffix}"),
                    GetFloat(document, $"{indexedPrefix}Depth")));
            bool decalHueEnabled = document.TryGetPositive($"_DecalHueShiftEnabled{suffix}");
            bool decalChromaEnabled =
                !document.HasAnyProperty($"{indexedPrefix}ChannelSeparationEnable") ||
                document.TryGetPositive($"{indexedPrefix}ChannelSeparationEnable");
            SetVector4(
                material,
                $"_DecalSlotFx{slot}",
                new Vector4(
                    decalHueEnabled ? GetFloat(document, $"_DecalHueShift{suffix}") : 0.0f,
                    decalHueEnabled ? GetFloat(document, $"_DecalHueShiftSpeed{suffix}") : 0.0f,
                    decalChromaEnabled ? GetFloat(document, $"{indexedPrefix}ChannelSeparation") : 0.0f,
                    GetInt(document, $"_DecalColor{suffix}ThemeIndex")));
            int decalMaskChannel = document.TryGetInt($"{indexedPrefix}MaskChannel", out int authoredDecalMaskChan)
                ? authoredDecalMaskChan
                : document.TryGetFloat($"{indexedPrefix}MaskChannel", out float authoredDecalMaskChanFloat)
                    ? (int)MathF.Round(authoredDecalMaskChanFloat)
                    : slot;
            SetVector4(
                material,
                $"_DecalSlotMask{slot}",
                new Vector4(
                    decalEnabled ? 1.0f : 0.0f,
                    Math.Clamp(decalMaskChannel, 0, 3),
                    GetFloat(document, $"_DecalTiled{suffix}"),
                    GetInt(document, $"_DecalSymmetryMode{suffix}")));
            SetVector4(
                material,
                $"_DecalSlotGlobalMask{slot}",
                new Vector4(
                    NormalizeSourceGlobalMaskBlendType(
                        GetInt(document, $"{indexedPrefix}GlobalMaskBlendType", 2),
                        sourceVersion),
                    decalApplyGlobalMask,
                    NormalizeSourceGlobalMaskBlendType(
                        GetInt(document, $"{indexedPrefix}ApplyGlobalMaskBlendType"),
                        sourceVersion),
                    GetInt(document, $"{indexedPrefix}OverrideAlphaMode")));
            Vector4 sideOffset = document.TryGetVector($"_DecalSideOffset{suffix}", out Vector4 authoredSideOffset)
                ? authoredSideOffset
                : Vector4.Zero;
            SetVector4(material, $"_DecalSideOffset{suffix}", sideOffset);
            if (document.Textures.TryGetValue($"_DecalTexture{suffix}", out SerializedTexturePropertyDocument? decalTexProp) &&
                decalTexProp is not null)
            {
                SetVector4(
                    material,
                    $"_DecalTexture{suffix}_ST",
                    new Vector4(decalTexProp.Scale.X, decalTexProp.Scale.Y, decalTexProp.Offset.X, decalTexProp.Offset.Y));
            }
            else if (material.Parameter<ShaderVector4>($"_DecalTexture{suffix}_ST") is null)
            {
                SetVector4(material, $"_DecalTexture{suffix}_ST", new Vector4(1.0f, 1.0f, 0.0f, 0.0f));
            }

            string emissionSuffix = slot == 0 ? string.Empty : slot.ToString();
            bool emissionEnabled = document.TryGetPositive($"_EnableEmission{emissionSuffix}");
            bool emissionHueEnabled =
                !document.HasAnyProperty($"_EmissionHueShiftEnabled{emissionSuffix}") ||
                document.TryGetPositive($"_EmissionHueShiftEnabled{emissionSuffix}");
            SetVector4(
                material,
                $"_EmissionSlotParams{slot}",
                new Vector4(
                    emissionEnabled ? GetFloat(document, $"_EmissionStrength{emissionSuffix}") : 0.0f,
                    GetFloat(document, $"_EmissionBlinkingEnabled{emissionSuffix}"),
                    GetFloat(document, $"_EmissiveBlink_Velocity{emissionSuffix}"),
                    emissionHueEnabled ? GetFloat(document, $"_EmissionHueShiftSpeed{emissionSuffix}") : 0.0f));
            SetVector4(
                material,
                $"_EmissionSlotColor{slot}",
                document.TryGetVector($"_EmissionColor{emissionSuffix}", out Vector4 authoredEmissionColor)
                    ? authoredEmissionColor
                    : Vector4.One);
            SetVector2(
                material,
                $"_EmissionSlotPan{slot}",
                document.TryGetVector($"_EmissionMap{emissionSuffix}Pan", out Vector4 authoredEmissionPan)
                    ? new Vector2(authoredEmissionPan.X, authoredEmissionPan.Y)
                    : Vector2.Zero);
            Vector2 emissionMaskPan =
                document.TryGetVector($"_EmissionMask{emissionSuffix}Pan", out Vector4 authoredMaskPan) ||
                document.TryGetVector($"_EmissionMask{slot}Pan", out authoredMaskPan)
                    ? new Vector2(authoredMaskPan.X, authoredMaskPan.Y)
                    : Vector2.Zero;
            SetVector2(material, $"_Emission{slot}MaskPan", emissionMaskPan);
            MapInt(document, material, $"_EmissionSlotUv{slot}", $"_EmissionMap{emissionSuffix}UV");
            if (material.Parameter<ShaderVector4>($"_Emission{slot}Tex_ST") is null)
                SetVector4(material, $"_Emission{slot}Tex_ST", new Vector4(1.0f, 1.0f, 0.0f, 0.0f));
            if (material.Parameter<ShaderVector4>($"_Emission{slot}Mask_ST") is null)
                SetVector4(material, $"_Emission{slot}Mask_ST", new Vector4(1.0f, 1.0f, 0.0f, 0.0f));
            float emissionReplace = document.TryGetFloat($"_EmissionReplace{slot}", out float authoredEmissionReplace)
                ? authoredEmissionReplace
                : GetFloat(document, $"_EmissionReplace{emissionSuffix}");
            int emissionGlobalMask = globalMasksEnabled
                ? ResolveIndexedOrSuffixedInt(
                    document,
                    $"_EmissionMask{slot}GlobalMask",
                    $"_EmissionMask{emissionSuffix}GlobalMask")
                : 0;
            int emissionGlobalMaskBlend = NormalizeSourceGlobalMaskBlendType(
                ResolveIndexedOrSuffixedInt(
                    document,
                    $"_EmissionMask{slot}GlobalMaskBlendType",
                    $"_EmissionMask{emissionSuffix}GlobalMaskBlendType",
                    fallback: 2),
                sourceVersion);
            int emissionMaskChannel = ResolveIndexedOrSuffixedInt(
                document,
                $"_EmissionMask{slot}Channel",
                $"_EmissionMask{emissionSuffix}Channel");
            int emissionMaskUv = ResolveIndexedOrSuffixedInt(
                document,
                $"_EmissionMask{slot}UV",
                $"_EmissionMask{emissionSuffix}UV");
            SetVector4(
                material,
                $"_EmissionSlotModifiers{slot}",
                new Vector4(
                    emissionReplace,
                    GetFloat(document, $"_EmissionCenterOutEnabled{emissionSuffix}"),
                    emissionGlobalMask,
                    GetInt(document, $"_EmissionColor{emissionSuffix}ThemeIndex")));
            SetVector4(
                material,
                $"_EmissionSlotSampling{slot}",
                new Vector4(
                    GetFloat(document, $"_EmissionBaseColorAsMap{emissionSuffix}"),
                    emissionMaskChannel,
                    GetFloat(document, $"_EmissionMaskInvert{emissionSuffix}"),
                    GetFloat(document, $"_EmissionFluorescence{emissionSuffix}")));
            SetVector4(
                material,
                $"_EmissionSlotExtra{slot}",
                new Vector4(
                    emissionGlobalMaskBlend,
                    emissionHueEnabled ? GetFloat(document, $"_EmissionHueShift{emissionSuffix}") : 0.0f,
                    emissionMaskUv,
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
                if (document.TryGetPositive($"{matcapPrefix}HueShiftEnabled") &&
                    GetInt(document, $"{matcapPrefix}HueSelectOrShift", 1) != 1)
                    throw new SourceVisualImportException($"Source matcap hue selection is not supported on '{document.Name}'.");
                foreach (string unsupported in new[] { "ApplyToAlphaEnabled", "AlphaOverride", "TPSDepthEnabled" })
                {
                    if (document.TryGetPositive($"{matcapPrefix}{unsupported}"))
                        throw new SourceVisualImportException($"Source matcap option '{matcapPrefix}{unsupported}' is enabled on '{document.Name}'.");
                }
                if (document.TryGetPositive($"_Matcap{slot}CircleMaskEnabled"))
                    throw new SourceVisualImportException($"A source matcap circle mask is enabled on '{document.Name}'.");
                bool audioLink = document.TryGetPositive($"_Matcap{slot}ALEnabled");
                if (audioLink && GetFloat(document, $"_Matcap{slot}ALChronoPanSpeed") != 0.0f)
                    throw new SourceVisualImportException($"Source matcap AudioLink chrono pan is not supported on '{document.Name}'.");
                float rotation = GetFloat(document, $"{matcapPrefix}Rotation");
                if (!float.IsFinite(rotation) || rotation < -1.0f || rotation > 1.0f)
                    throw new SourceVisualImportException($"Source matcap rotation is outside its supported range on '{document.Name}'.");
                bool customNormal = document.TryGetPositive($"_Matcap{slot}CustomNormal");
                if (customNormal && (slot != 1 || !HasExternalTexture(document, "_Matcap1NormalMap") ||
                    GetInt(document, "_Matcap1NormalMapUV") != 0 ||
                    GetFloat(document, "_Matcap2Normal", 1.0f) != 1.0f))
                    throw new SourceVisualImportException($"A source matcap custom normal is not supported on '{document.Name}'.");
                int globalMask = GetInt(document, $"{matcapPrefix}MaskGlobalMask");
                int globalBlend = NormalizeSourceGlobalMaskBlendType(
                    GetInt(document, $"{matcapPrefix}MaskGlobalMaskBlendType", 2),
                    sourceVersion);
                bool hasGlobalMaskTexture = false;
                if (globalMask is >= 1 and <= 16)
                {
                    string maskName = $"_GlobalMaskTexture{(globalMask - 1) / 4}";
                    hasGlobalMaskTexture = HasExternalTexture(document, maskName) ||
                        HasWhiteShaderTextureDefault(resolver.Resolve(document.Shader.Guid), maskName);
                }
                if (globalMask != 0 && (globalMask < 1 || globalMask > 16 ||
                    globalBlend < 0 || globalBlend > 7 ||
                    !hasGlobalMaskTexture && globalMasksEnabled ||
                    !globalMasksEnabled && globalBlend != 2))
                    throw new SourceVisualImportException($"Source matcap global mask '{matcapPrefix}MaskGlobalMask' is not supported on '{document.Name}'.");
                if (!globalMasksEnabled && globalBlend == 2)
                    globalMask = 0;
                string blendProperty = slot switch
                {
                    0 => "_MatCapBlendUV1",
                    1 => "_MatCap2ndBlendUV1",
                    2 => "_MatCap3rdBlendUV1",
                    _ => "_MatCap4thBlendUV1",
                };
                Vector4 blendUv = document.TryGetVector(blendProperty, out Vector4 authoredBlendUv)
                    ? authoredBlendUv : Vector4.Zero;
                int blendUvIndex = GetInt(document, $"{matcapPrefix}UVToBlend");
                if ((blendUv.X != 0.0f || blendUv.Y != 0.0f) &&
                    (blendUvIndex < 0 || blendUvIndex > 3 ||
                     !float.IsFinite(blendUv.X) || !float.IsFinite(blendUv.Y) ||
                     blendUv.X < 0.0f || blendUv.X > 1.0f ||
                     blendUv.Y < 0.0f || blendUv.Y > 1.0f))
                    throw new SourceVisualImportException($"Source matcap UV blend '{blendProperty}' is not supported on '{document.Name}'.");
                SetVector4(material, $"_MatcapSlotUvBlend{slot}", new Vector4(
                    blendUv.X, blendUv.Y, customNormal ? 1.0f : 0.0f,
                    customNormal ? GetFloat(document, "_Matcap1NormalMapScale", 1.0f) : 0.0f));
                SetVector4(material, $"_MatcapSlotUvControl{slot}", new Vector4(
                    blendUvIndex,
                    sourceVersion == SourceToon81Catalog.Version ? 0.0f : 1.0f,
                    globalMask, globalBlend));
                SetFloat(material, $"_MatcapSlotRotation{slot}", rotation);
                SetVector4(material, $"_MatcapSlotAudioBands{slot}", new Vector4(
                    audioLink ? 1.0f : 0.0f,
                    GetSourceAudioBand(document, $"_Matcap{slot}ALAlphaAddBand", audioLink),
                    GetSourceAudioBand(document, $"_Matcap{slot}ALEmissionAddBand", audioLink),
                    GetSourceAudioBand(document, $"_Matcap{slot}ALIntensityAddBand", audioLink)));
                SetVector2(material, $"_MatcapSlotAudioAlpha{slot}",
                    GetSourceAudioRange(document, $"_Matcap{slot}ALAlphaAdd"));
                SetVector2(material, $"_MatcapSlotAudioEmission{slot}",
                    GetSourceAudioRange(document, $"_Matcap{slot}ALEmissionAdd"));
                SetVector2(material, $"_MatcapSlotAudioIntensity{slot}",
                    GetSourceAudioRange(document, $"_Matcap{slot}ALIntensityAdd"));
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
            SetVector4(material, $"_MatcapSlotHue{slot}", new Vector4(
                matcapEnabled && document.TryGetPositive($"{matcapPrefix}HueShiftEnabled") ? 1.0f : 0.0f,
                GetFloat(document, $"{matcapPrefix}HueShift"),
                GetFloat(document, $"{matcapPrefix}HueShiftSpeed"),
                GetFloat(document, $"{matcapPrefix}HueShiftColorSpace")));
            SetVector4(material, $"_MatcapSlotMask{slot}", new Vector4(
                GetInt(document, $"{matcapPrefix}MaskChannel"),
                GetFloat(document, $"{matcapPrefix}MaskInvert"),
                GetInt(document, $"{matcapPrefix}MaskSmoothnessChannel", 3),
                GetInt(document, $"{matcapPrefix}MaskUV")));
        }

        bool rim2Enabled = document.TryGetPositive("_EnableRim2Lighting");
        int rim2Style = GetInt(document, "_Rim2Style");
        if (rim2Style is 0 or 1)
            MapVector4(document, material, "_Rim2Color", "_Rim2LightColor", "_Rim2Color");
        else
            MapVector4(document, material, "_Rim2Color", "_Rim2Color", "_Rim2LightColor");

        float rim2Sharpness = rim2Style == 0
            ? GetFloat(document, "_Rim2Power", 1.0f)
            : GetFloat(document, "_Rim2Sharpness", 1.0f);
        float rim2Intensity = 0.0f;
        if (rim2Enabled)
        {
            rim2Intensity = document.TryGetFloat("_Rim2Intensity", out float authoredRim2Intensity)
                ? authoredRim2Intensity
                : GetFloat(document, "_Rim2BlendStrength", 1.0f);
        }

        SetInt(material, "_Rim2Enable", rim2Enabled ? 1 : 0);
        SetInt(material, "_Rim2MaskChannel", GetInt(document, "_Rim2MaskChannel"));
        SetInt(material, "_Rim2LightColorThemeIndex", GetInt(document, "_Rim2LightColorThemeIndex"));
        SetVector4(
            material,
            "_Rim2Params",
            new Vector4(
                GetFloat(document, "_Rim2Width"),
                rim2Sharpness,
                GetFloat(document, "_Rim2HideInShadow"),
                rim2Intensity));
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
        float rimBlur = GetFloat(document, "_RimBlur", -1.0f);
        SetVector4(material, "_SourceRimColorSettings", new Vector4(
            GetFloat(document, "_RimBaseColorMix"),
            GetFloat(document, "_RimBrightness", 1.0f),
            GetInt(document, "_RimPoiBlendMode"),
            // Zero selects the older sharpness edge. Other values store the source blur plus one.
            rimBlur >= 0.0f ? rimBlur + 1.0f : 0.0f));

        bool rim2Enabled = document.TryGetPositive("_EnableRim2Lighting");
        string? unsupportedRim2Option = rim2Enabled ? GetUnsupportedSourceRim2Option(document) : null;
        if (unsupportedRim2Option is not null)
        {
            AddDiagnostic(diagnostics, warnings,
                MaterialConversionDiagnosticCodes.IntentionalNativeDifference,
                $"Source second rim option '{unsupportedRim2Option}' on '{document.Name}' uses the approximate Uber shading path.",
                unsupportedRim2Option);
        }
        SetVector4(material, "_SourceRim2Settings", new Vector4(
            rim2Enabled && unsupportedRim2Option is null ? 1.0f : 0.0f,
            GetFloat(document, "_Rim2LightingInvert"),
            GetFloat(document, "_Rim2Power", 1.0f),
            GetFloat(document, "_Is_NormalMapToRim2Light", 1.0f)));
        float rim2Blur = GetFloat(document, "_Rim2Blur", -1.0f);
        SetVector4(material, "_SourceRim2ColorSettings", new Vector4(
            GetFloat(document, "_Rim2BaseColorMix"),
            GetFloat(document, "_Rim2Brightness", 1.0f),
            GetInt(document, "_RimPoi2BlendMode"),
            rim2Blur >= 0.0f ? rim2Blur + 1.0f : 0.0f));
        SetVector4(material, "_SourceRim2Params", new Vector4(
            GetFloat(document, "_Rim2Width"),
            GetFloat(document, "_Rim2Sharpness"),
            GetFloat(document, "_Rim2BlendStrength", 1.0f),
            GetFloat(document, "_Rim2Strength")));
    }

    private static bool SupportsExactSourceRim(SerializedMaterialDocument document)
        => document.TryGetPositive("_EnableRimLighting") &&
           GetUnsupportedSourceRimOption(document) is null;

    private static bool SupportsExactSourceRim2(SerializedMaterialDocument document)
        => document.TryGetPositive("_EnableRim2Lighting") &&
           GetUnsupportedSourceRim2Option(document) is null;

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

    private static string? GetUnsupportedSourceRim2Option(SerializedMaterialDocument document)
    {
        if (GetInt(document, "_Rim2Style") != 0)
            return "_Rim2Style";
        foreach (string property in new[]
        {
            "_Rim2ShadowToggle", "_Rim2GlobalMask", "_Rim2ApplyAlpha",
            "_Rim2ApplyGlobalMaskIndex", "_Rim2BiasIntensity", "_Rim2MaskInvert"
        })
        {
            if (document.TryGetPositive(property))
                return property;
        }
        if (document.TryGetPositive("_Rim2HueShiftEnabled") &&
            (MathF.Abs(GetFloat(document, "_Rim2HueShift")) > 0.0001f ||
             MathF.Abs(GetFloat(document, "_Rim2HueShiftSpeed")) > 0.0001f ||
             GetInt(document, "_Rim2HueSelectOrShift", 1) != 1))
            return "_Rim2HueShiftEnabled";
        foreach (string property in new[] { "_Rim2MaskPan", "_Rim2TexPan" })
        {
            if (document.TryGetVector(property, out Vector4 pan) &&
                (MathF.Abs(pan.X) > 0.0001f || MathF.Abs(pan.Y) > 0.0001f))
                return property;
        }
        return null;
    }

    private static int ResolveIndexedOrSuffixedInt(
        SerializedMaterialDocument document,
        string indexedName,
        string suffixedName,
        int fallback = 0)
    {
        if (document.TryGetInt(indexedName, out int indexedValue))
            return indexedValue;
        if (document.TryGetFloat(indexedName, out float indexedFloat))
            return (int)MathF.Round(indexedFloat);
        if (document.TryGetInt(suffixedName, out int suffixedValue))
            return suffixedValue;
        if (document.TryGetFloat(suffixedName, out float suffixedFloat))
            return (int)MathF.Round(suffixedFloat);
        return fallback;
    }

    /// <summary>
    /// Converts source toon 8.x 20-entry <c>customBlend</c> global-mask blend values into the
    /// canonical 8-entry <c>maskBlend</c> table used by source toon 9.x and XRENGINE shaders.
    /// </summary>
    private static int NormalizeSourceGlobalMaskBlendType(int rawBlend, SourceToonShaderVersion? sourceVersion)
    {
        if (sourceVersion == SourceToon81Catalog.Version || rawBlend > 7)
        {
            return rawBlend switch
            {
                0 => 0,
                1 => 4,
                2 => 2,
                5 => 5,
                8 => 7,
                18 => 1,
                19 => 3,
                _ => rawBlend,
            };
        }

        return rawBlend;
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

        SetVector4(material, "_GlobalMaskMin", Vector4.Zero);
        SetVector4(material, "_GlobalMaskMax", Vector4.One);
        SetVector4(material, "_GlobalMaskInvert", Vector4.Zero);

        bool postProcessEnabled =
            document.TryGetPositive("_PostProcess") ||
            document.ValidKeywords.Contains("POSTPROCESS");
        if (postProcessEnabled)
            material.SetUberFeatureEnabled("surface-extensions", true);
        SetVector4(
            material,
            "_SourcePostProcess",
            new Vector4(
                postProcessEnabled ? 1.0f : 0.0f,
                GetFloat(document, "_PPBrightness", 1.0f),
                GetFloat(document, "_PPContrast", 1.0f),
                GetFloat(document, "_PPSaturation", 1.0f)));
        SetVector4(
            material,
            "_SourcePostProcessHue",
            new Vector4(
                GetFloat(document, "_PPHue"),
                GetFloat(document, "_PPLightness"),
                GetFloat(document, "_PPHDR"),
                GetFloat(document, "_PPHueShiftColorSpace")));
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
        List<string> warnings,
        SourceToonShaderVersion? sourceVersion)
    {
        (string Destination, string[] Sources)[] bindings =
        [
            ("_MainTexDistortionMap", ["_MainTexDistortionMap"]),
            ("_MainTexDistortionMask", ["_MainTexDistortionMask"]),
            ("_BackFaceNormalMap", ["_BackFaceNormalMap"]),
            ("_ColorMaskTexture", ["_ColorMask"]),
            ("_GlobalMaskTexture0", ["_GlobalMaskTexture0", "_GlobalMaskTexture"]),
            ("_GlobalMaskTexture1", ["_GlobalMaskTexture1"]),
            ("_GlobalMaskTexture2", ["_GlobalMaskTexture2"]),
            ("_GlobalMaskTexture3", ["_GlobalMaskTexture3"]),
            ("_DetailShadowMap", ["_DetailShadowMap"]),
            ("_LightingSDFMap", ["_LightingSDFMap", "_SDFLightingTexture"]),
            ("_Specular2Map", ["_Specular2Map"]),
            ("_BacklightMask", ["_BacklightMask"]),
            ("_CubeMapMask", ["_CubeMapMask"]),
            ("_RimEnviroMask", ["_RimEnviroMask"]),
            ("_DecalMask", ["_DecalMask"]),
            ("_DecalTexture", ["_DecalTexture"]),
            ("_DecalTexture1", ["_DecalTexture1"]),
            ("_DecalTexture2", ["_DecalTexture2"]),
            ("_DecalTexture3", ["_DecalTexture3"]),
            ("_Matcap0Tex", ["_Matcap"]),
            ("_Matcap0Mask", ["_MatcapMask"]),
            ("_Matcap1Tex", ["_Matcap2"]),
            ("_Matcap1Mask", ["_Matcap2Mask"]),
            ("_Matcap1NormalMap", ["_Matcap1NormalMap"]),
            ("_Matcap2Tex", ["_Matcap3"]),
            ("_Matcap2Mask", ["_Matcap3Mask"]),
            ("_Matcap3Tex", ["_Matcap4"]),
            ("_Matcap3Mask", ["_Matcap4Mask"]),
            ("_Rim2Mask", ["_Rim2Mask"]),
            ("_Rim2ColorTexture", SupportsExactSourceRim2(document) ? ["_Rim2Tex"] : ["_Rim2ColorTex", "_Rim2Tex"]),
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

        bool globalMasksEnabled = document.TryGetPositive("_GlobalMaskTexturesEnable");
        foreach ((string destination, string[] sources) in bindings)
        {
            if (!globalMasksEnabled &&
                destination is "_GlobalMaskTexture0" or "_GlobalMaskTexture1" or "_GlobalMaskTexture2" or "_GlobalMaskTexture3")
                continue;

            foreach (string source in sources)
            {
                bool sourceMatcapSampler = (destination is
                    "_Matcap0Tex" or "_Matcap1Tex" or "_Matcap2Tex" or "_Matcap3Tex") &&
                    sourceVersion != SourceToon81Catalog.Version;
                XRTexture2D? texture = ResolveUberTexture(document, resolver, warnings,
                    source, destination, sourceMatcapSampler);
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
            using RuntimeImage source = RuntimeImageCodecs.Require().Decode(File.ReadAllBytes(texturePath), texturePath);
            var baseMipmap = new CubeMipmap();
            SerializedTextureImportDocument? settings = SerializedTextureImportDocumentParser.ParseFile(texturePath);
            if (settings is { RawShape: not 2 })
                throw new InvalidDataException("Unity texture metadata does not declare a cubemap shape.");
            int mode = settings?.GenerateCubemap ?? 6;
            bool mirroredBall = mode == 1 || mode == 6 && source.Width == source.Height;
            bool converted = mode switch
            {
                1 when source.Width == source.Height => baseMipmap.SetMirroredBallMap(source, settings?.IsSrgb ?? true),
                2 when source.Width == source.Height * 2 => baseMipmap.SetEquirectangularMap(source),
                5 => baseMipmap.SetCrossCubeMap(source),
                6 when source.Width == source.Height => baseMipmap.SetMirroredBallMap(source, settings?.IsSrgb ?? true),
                6 when source.Width == source.Height * 2 => baseMipmap.SetEquirectangularMap(source),
                6 => baseMipmap.SetCrossCubeMap(source),
                _ => false,
            };
            if (!converted)
                throw new InvalidDataException($"Unity cubemap layout {mode} does not match the source image.");

            CubeMipmap[] mipmaps = CreateCubemapMipChain(
                baseMipmap,
                settings?.GenerateMipMaps is not false);
            bool isSrgb = !mirroredBall && settings?.IsSrgb is true;
            var cubemap = new XRTextureCube(mipmaps)
            {
                Name = Path.GetFileNameWithoutExtension(texturePath),
                SamplerName = "_CubeMap",
                SizedInternalFormat = isSrgb ? ESizedInternalFormat.Srgb8Alpha8
                    : XRTexture2D.DeriveESizedInternalFormat(baseMipmap.Sides[0].InternalFormat),
                ImportedColorSpace = isSrgb ? ETextureColorSpace.Srgb : ETextureColorSpace.Linear,
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

        try
        {
            SerializedTextureImportDocument? settings = SerializedTextureImportDocumentParser.ParseFile(texturePath);
            XRTexture2DArray texture;
            if (Path.GetExtension(texturePath).Equals(".asset", StringComparison.OrdinalIgnoreCase))
            {
                texture = SerializedTexture2DArrayDecoder.DecodeFile(texturePath);
            }
            else if (Path.GetExtension(texturePath).Equals(".gif", StringComparison.OrdinalIgnoreCase))
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
            if (settings is not null && !Path.GetExtension(texturePath).Equals(".asset", StringComparison.OrdinalIgnoreCase))
            {
                foreach (XRTexture2D frame in texture.Textures)
                    ApplySerializedTextureImportSettings(frame, settings, "_FlipbookTexArray");
            }
            ReplaceMaterialSampler(material, "_FlipbookTexArray", texture);
            ApplyTextureTransform(material, document, "_FlipbookTexArray", "_FlipbookTexArray");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
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
