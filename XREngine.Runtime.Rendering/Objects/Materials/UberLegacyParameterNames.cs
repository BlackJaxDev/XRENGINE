using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace XREngine.Rendering;

/// <summary>
/// Maps earlier Uber parameter and sampler names to their current names.
/// Saved materials can contain the earlier names. <see cref="XRMaterial"/> renames them when it initializes the Uber state.
/// </summary>
internal static class UberLegacyParameterNames
{
    private static readonly FrozenDictionary<string, string> Renames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["_PBRMochiePackedMasksEnabled"] = "_PBRPackedMasksEnabled",
        ["_SourceAdditiveColor"] = "_AdditiveLightColor",
        ["_SourceAdditiveGradient"] = "_AdditiveLightGradient",
        ["_SourceAdditiveSettings"] = "_AdditiveLightSettings",
        ["_SourceAlphaClip"] = "_AlphaClipAllModes",
        ["_SourceAnisoLayer0"] = "_AnisotropyLayer0",
        ["_SourceAnisoLayer1"] = "_AnisotropyLayer1",
        ["_SourceAnisoMapSettings"] = "_AnisotropyMapSettings",
        ["_SourceAnisoMix"] = "_AnisotropyMix",
        ["_SourceAnisoOffsetMapStrength"] = "_AnisotropyOffsetMapStrength",
        ["_SourceAnisoSettings"] = "_AnisotropySettings",
        ["_SourceAnisoTint0"] = "_AnisotropyTint0",
        ["_SourceAnisoTint1"] = "_AnisotropyTint1",
        ["_SourceAnisoToon0"] = "_AnisotropyToon0",
        ["_SourceAnisoToon1"] = "_AnisotropyToon1",
        ["_SourceBackfaceNormals"] = "_BackfaceNormalControl",
        ["_SourceClearCoat"] = "_ClearCoatSettings",
        ["_SourceClearCoatGsaa"] = "_ClearCoatAASettings",
        ["_SourceClearCoatMaps"] = "_ClearCoatMaskMaps",
        ["_SourceClearCoatMapsPan"] = "_ClearCoatMaskMapsPan",
        ["_SourceClearCoatMapsUV"] = "_ClearCoatMaskMapsUV",
        ["_SourceClearCoatMaps_ST"] = "_ClearCoatMaskMaps_ST",
        ["_SourceClearCoatReflection"] = "_ClearCoatReflection",
        ["_SourceCubeMap"] = "_CubeMapToonModel",
        ["_SourceDecalAlphaParams0"] = "_DecalAlphaParams0",
        ["_SourceDecalAlphaParams1"] = "_DecalAlphaParams1",
        ["_SourceDecalAlphaParams2"] = "_DecalAlphaParams2",
        ["_SourceDecalAlphaParams3"] = "_DecalAlphaParams3",
        ["_SourceDissolveParams"] = "_DissolveSettings",
        ["_SourceEnviroRim"] = "_EnviroRimSettings",
        ["_SourceFlipbook"] = "_FlipbookToonModel",
        ["_SourceFlipbookFlags"] = "_FlipbookFlags",
        ["_SourceFlipbookHue"] = "_FlipbookHue",
        ["_SourceFlipbookLayout"] = "_FlipbookLayout",
        ["_SourceFlipbookMotion"] = "_FlipbookMotion",
        ["_SourceFlipbookOptions"] = "_FlipbookOptions",
        ["_SourceFlipbookSides"] = "_FlipbookSides",
        ["_SourceGlitter"] = "_GlitterModel",
        ["_SourceGlitterVersion"] = "_GlitterFlakeShading",
        ["_SourceGrabAddBrightness"] = "_SceneGrabAddBrightness",
        ["_SourceGrabBlendFactor"] = "_SceneGrabBlendFactor",
        ["_SourceGrabBrightness"] = "_SceneGrabBrightness",
        ["_SourceGrabColor"] = "_SceneGrabColor",
        ["_SourceGrabDstBlend"] = "_SceneGrabDstBlend",
        ["_SourceGrabPassBlendMode"] = "_SceneGrabBlendMode",
        ["_SourceGrabSrcBlend"] = "_SceneGrabSrcBlend",
        ["_SourceGrabUseAlpha"] = "_SceneGrabUseAlpha",
        ["_SourceHighColorMask"] = "_HighColorMask",
        ["_SourceHighColorMask_ST"] = "_HighColorMask_ST",
        ["_SourceHighColorTex"] = "_HighColorTex",
        ["_SourceHighColorTex_ST"] = "_HighColorTex_ST",
        ["_SourceLayeredEmission"] = "_EmissionToonModel",
        ["_SourceLightMode"] = "_LightModel",
        ["_SourceLightSettings"] = "_ToonLightSettings",
        ["_SourceMatcap"] = "_MatcapToonModel",
        ["_SourceMochieReflection"] = "_ReflectionResponse",
        ["_SourceMochieReflectionTint"] = "_ReflectionTint",
        ["_SourceMochieSecond"] = "_SecondSpecularSettings",
        ["_SourceMochieSettings"] = "_SpecularAASettings",
        ["_SourceMultilayerBlurs"] = "_MultilayerBlurs",
        ["_SourceMultilayerBorders"] = "_MultilayerBorders",
        ["_SourceMultilayerFlat"] = "_MultilayerFlat",
        ["_SourceMultilayerFlatAxis"] = "_MultilayerFlatAxis",
        ["_SourceMultilayerMix"] = "_MultilayerMix",
        ["_SourceMultilayerOptions"] = "_MultilayerOptions",
        ["_SourceMultilayerReceive"] = "_MultilayerReceive",
        ["_SourceOutlineBasic"] = "_OutlineBasicModel",
        ["_SourceRefractionChromaticAberration"] = "_SceneRefractionChromaticAberration",
        ["_SourceRefractionEnabled"] = "_SceneRefractionEnabled",
        ["_SourceRefractionFresnelPower"] = "_SceneRefractionFresnelPower",
        ["_SourceRefractionIndex"] = "_SceneRefractionIndex",
        ["_SourceRim2ColorSettings"] = "_Rim2ColorSettings",
        ["_SourceRim2Settings"] = "_Rim2Settings",
        ["_SourceRim2Strengths"] = "_Rim2Strengths",
        ["_SourceRim2Tint"] = "_Rim2Tint",
        ["_SourceRimColorSettings"] = "_RimColorSettings",
        ["_SourceRimSettings"] = "_RimShapeSettings",
        ["_SourceSceneColor"] = "_SceneColorTexture",
        ["_SourceSceneColorMode"] = "_SceneColorMode",
        ["_SourceShadowBorderMaskLod"] = "_ShadowBorderMaskTrilinear",
        ["_SourceStylizedSpecularLayers"] = "_StylizedSpecularLayers",
        ["_SourceStylizedSpecularMask"] = "_StylizedSpecularMask",
        ["_SourceStylizedSpecularOcclusion"] = "_StylizedSpecularOcclusion",
        ["_SourceStylizedSpecularSettings"] = "_StylizedSpecularSettings",
        ["_SourceStylizedSpecularTint"] = "_StylizedSpecularTint",
        ["_SourceUdimFlipV"] = "_UdimFlipV",
        ["_SourceWrappedColor"] = "_WrappedLightColor",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Gets the current name for an earlier Uber parameter or sampler name.
    /// </summary>
    /// <param name="name">The name to look up.</param>
    /// <param name="currentName">The current name, when <paramref name="name"/> is an earlier name.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> is an earlier name.</returns>
    public static bool TryGetCurrentName(string? name, [NotNullWhen(true)] out string? currentName)
    {
        currentName = null;
        // All earlier names start with "_Source" or "_PBRMochie". Skip the lookup for other names.
        if (name is null || name.Length < 8 || name[0] != '_' || (name[1] != 'S' && name[1] != 'P'))
            return false;

        return Renames.TryGetValue(name, out currentName);
    }
}
