using System.Text;
using XREngine.Rendering.Materials;

namespace XREngine.Rendering;

/// <summary>Stable native Uber constants and texture roles shared by all material rows.</summary>
public static class NativeUberMaterialSchema
{
    private const string ScalarNames = "_AlphaMaskBlendStrength _AlphaMaskInvert _AlphaMaskValue _AlphaMod _BackFaceAlpha _BackFaceColor _BackFaceEmission _BumpScale _Color _ColorMaskEnabled _ColorThemeIndex _CubeMapBlendAmount _CubeMapBlendType _CubeMapEmissionStrength _CubeMapLightMask _CubeMapUvMode _DepthAlphaEnabled _DetailBrightness _DetailNormalMapScale _DetailTexIntensity _DetailTint _DissolveContinuous _DissolveCoordinateSpace _DissolveCutoff _DissolveDetailStrength _DissolveEdgeColor _DissolveEdgeEmission _DissolveEdgeWidth _DissolveEndPoint _DissolveHueShift _DissolveInvert _DissolveMaskInvert _DissolveNoiseStrength _DissolveProgress _DissolveStartPoint _DissolveTileGrid _DissolveType _EmissionColor _EmissionColorThemeIndex _EmissionMapPan _EmissionScrollingSpeed _EmissionScrollingVertexColor _EmissionStrength _FaceDiscard _FlipbookColumns _FlipbookCrossfade _FlipbookFrameRate _FlipbookManualFrame _FlipbookRows _ForceFlatRampedLightmap _GlitterBrightness _GlitterColor _GlitterDensity _GlitterMaxAngle _GlitterMinAngle _GlitterRainbow _GlitterSize _GlitterSpeed _InternalParallaxParams _InternalParallaxStrength _LightDataAOStrengthR _LightingCap _LightingCapEnabled _LightingColorMode _LightingDirectionMode _LightingGradientEnd _LightingGradientStart _LightingIgnoreAmbientColor _LightingIndirectUsesNormals _LightingMapMode _LightingMinLightBrightness _LightingMode _LightingMonochromatic _LightingShadowColor _LightingShadowMaskStrengthR _LightingWrappedNormalization _LightingWrappedWrap _MainAlphaMaskMode _MainBrightness _MainHueShift _MainHueShiftColorSpace _MainHueShiftReplace _MainHueShiftSpeed _MainHueShiftToggle _MainUseVertexColorAlpha _MainVertexColoring _MainVertexColoringEnabled _MainVertexColoringLinearSpace _MatcapAdd _MatcapBorder _MatcapColor _MatcapEmissionStrength _MatcapIntensity _MatcapLightMask _MatcapMaskChannel _MatcapMaskInvert _MatcapMultiply _MatcapNormal _MatcapReplace _MatcapUVMode _OutlineDropShadowOffset _OutlineExpansionMode _OutlineFixedSize _OutlineHueShift _OutlineHueShiftSpeed _OutlineMaskPan _OutlineMaskUV _OutlinePersonaDirection _OutlineShadowStrength _OutlineSpace _OutlineTexturePan _OutlineTextureUV _OutlineUseVertexColors _OutlineZOffset _ParallaxMapChannel _ParallaxMaxSamples _ParallaxMinSamples _ParallaxMode _ParallaxOffset _ParallaxStrength _PathingColor _PathingParams _PathingStrength _PBRMetallicMapInvert _PBRMetallicMapsMetallicChannel _PBRMetallicMultiplier _PBRRoughnessMultiplier _PBRSmoothnessMapInvert _PBRSmoothnessMapsChannel _ProceduralColor _ProceduralMode _ProceduralParams _ProximityColor _ProximityParams _ProximityStrength _Rim2Color _RimBlendMode _RimBlendStrength _RimEmission _RimHideInShadow _RimLightColor _RimLightColorBias _RimMaskChannel _RimSharpness _RimStyle _RimWidth _Saturation _ShadowBlur _ShadowBorder _ShadowColor _ShadowOffset _ShadowStrength _SpecularSmoothness _SpecularStrength _SpecularTint _SpecularType _SSSAmbient _SSSColor _SSSDistortion _SSSPower _SSSScale _TouchGlowColor _TouchGlowParams _TouchGlowStrength _UvTileDiscard _UvTileDiscardGrid _UvTileDiscardRange _VertexBarrelAlpha _VertexBarrelHeight _VertexBarrelMode _VertexBarrelWidth _VertexColorNormalOffset _VertexColorPositionOffset _VertexConservativeBounds _VertexDepthBulge _VertexEffectsEnabled _VertexEquation _VertexGlitch _VertexLookAtAxis _VertexLookAtWeight _VertexManipulationHeight _VertexManipulationLocalRotation _VertexManipulationLocalRotationSpeed _VertexManipulationLocalScale _VertexManipulationLocalTranslation _VertexManipulationWorldTranslation _VertexRoundingDivision _VertexRoundingEnabled _VertexWave _VideoBlend";
    private const string SamplerNames = "_MainTex _BumpMap _AlphaMask _BackFaceNormalMap _BackFaceTexture _BacklightMask _ColorMaskTexture _CubeMapMask _DecalMask _DecalTexture _DetailMask _DetailNormalMap _DetailShadowMap _DetailTex _DissolveDetailNoise _DissolveEdgeGradient _DissolveEdgeTexture _DissolveMask _DissolveNoiseTexture _Emission0Mask _Emission0Tex _Emission1Mask _Emission1Tex _Emission2Mask _Emission2Tex _Emission3Mask _Emission3Tex _EmissionMap _FirstShadeMap _FlipbookMask _FlipbookTexture _GlitterMask _LightingAOMaps _LightingSDFMap _LightingShadowMasks _MainColorAdjustTexture _MainTexDistortionMap _MainTexDistortionMask _Matcap _Matcap0Mask _Matcap0Tex _Matcap1Mask _Matcap1Tex _Matcap2Mask _Matcap2Tex _Matcap3Mask _Matcap3Tex _MatcapMask _OutlineMask _OutlineTexture _ParallaxMap _PBRMetallicMaps _PBRSmoothnessMaps _Rim2Mask _RimColorTexture _RimMask _SecondShadeMap _ShadowColorTex _Specular2Map _SpecularMap _SSSThicknessMap _ToonRamp _VideoTexture _CubeMap _LightingDetailShadowMap _DecalTexture1 _DecalTexture2 _DecalTexture3 _GlobalMaskTexture0 _GlobalMaskTexture1 _GlobalMaskTexture2 _GlobalMaskTexture3";

    private static readonly string[] Samplers = [.. SamplerNames.Split(' '), "_SourceClearCoatMaps", "_RimEnviroMask", "_Shadow2ndColorTex", "_Shadow3rdColorTex", "_MultilayerMathBlurMap", "_ShadowStrengthMask", "_ShadowBorderMask", "_SourceShadowBorderMaskLod", "_GlitterTexture", "_Matcap1NormalMap", "_GlitterColorMap", "_FlipbookTexArray"];
    private static readonly string[] Names = BuildParameterNames();
    private static readonly string[] UdimCells = BuildUdimCells();
    private static readonly string[] Features = [
        "normal-map", "stylized-shading", "alpha-masks", "color-adjustments",
        "material-ao", "shadow-masks", "emission", "matcap", "rim-lighting",
        "advanced-specular", "detail-textures", "surface-extensions",
        "advanced-stylized-lighting", "advanced-pbr", "layered-matcap-rim",
        "layered-emission", "extended-effects", "dissolve", "outline",
        "backface", "glitter", "flipbook", "subsurface", "parallax",
        "global-masks-themes", "vertex-effects", "layered-decals", "texture-array-flipbook"];

    public static MaterialBindingLayout Opaque { get; } = Create("NativeUberOpaqueV1", MaterialBindingLayouts.ForwardOpaque);
    public static MaterialBindingLayout Masked { get; } = Create("NativeUberMaskedV1", MaterialBindingLayouts.MaskedForward);
    private static readonly Dictionary<string, int> ParameterIndices = BuildIndices(Names);
    private static readonly Dictionary<string, int> TextureIndices = BuildIndices(Samplers);
    private static readonly Dictionary<string, int> FeatureIndices = BuildIndices(Features);
    public static ReadOnlySpan<string> Parameters => Names;
    public static ReadOnlySpan<string> TextureRoles => Samplers;
    public static ReadOnlySpan<string> UdimCellNames => UdimCells;
    public static int ParameterCount => Names.Length;
    public static int TextureRoleCount => Samplers.Length;
    public static ReadOnlySpan<string> FeatureIds => Features;

    public static bool IsLayout(MaterialBindingLayout layout)
        => ReferenceEquals(layout, Opaque) || ReferenceEquals(layout, Masked);

    public static int ParameterIndex(string name) => ParameterIndices.TryGetValue(name, out int index) ? index : -1;
    public static int TextureIndex(string name) => TextureIndices.TryGetValue(name, out int index) ? index : -1;
    public static int FeatureIndex(string name) => FeatureIndices.TryGetValue(name, out int index) ? index : -1;
    public static int ParameterWordOffset(int index) => checked(FeatureWordOffset + 4 * (index + 1));
    public static int FeatureWordOffset => (TextureRoleCount + 3) & ~3;

    /// <summary>Appends offsets for the fixed native Uber material ABI.</summary>
    public static void AppendShaderDefines(StringBuilder source)
    {
        source.Append("#define XR_UBER_FEATURES_WORD ").Append(FeatureWordOffset).AppendLine("u");
        source.Append("#define XR_UBER_WORD_COUNT ").Append(Opaque.RowWordCount).AppendLine("u");
        AppendLayoutHash(source, "XR_UBER_OPAQUE_LAYOUT", Opaque.LayoutHash);
        AppendLayoutHash(source, "XR_UBER_MASKED_LAYOUT", Masked.LayoutHash);
        for (int index = 0; index < Features.Length; ++index)
            source.Append("#define XR_UBER_FEATURE_")
                .Append(Features[index].Replace('-', '_').ToUpperInvariant()).Append(' ')
                .Append(1u << index).AppendLine("u");
        for (int index = 0; index < Names.Length; ++index)
        {
            source.Append("#define XR_UBER_WORD").Append(Names[index]).Append(' ')
                .Append(ParameterWordOffset(index)).AppendLine("u");
        }
        for (int index = 0; index < Samplers.Length; ++index)
            source.Append("#define XR_UBER_TEX").Append(Samplers[index]).Append(' ')
                .Append(index).AppendLine("u");
    }

    private static string[] BuildParameterNames()
    {
        List<string> names = [.. ScalarNames.Split(' '), "_Mode", "_Cutoff", "_AlphaForceOpaque", "_AlphaSource", "_AlphaMaskChannel", "_MainTexUV", "_CubeMapColor", "_CubeMapStrength", "_CubeMapSmoothness", "_CubeMapCoordinateZSign", "_CubeMapMaskChannel", "_CubeMapMaskInvert", "_Anisotropy", "_AnisotropyRotation", "_Specular2Color", "_Specular2Smoothness", "_Specular2Strength", "_ClearCoat", "_ClearCoatSmoothness", "_RimEnviroSharpness", "_RimEnviroWidth", "_RimEnviroIntensity", "_Rim2Enable", "_RimLightColorThemeIndex", "_BacklightColor", "_BacklightStrength", "_StylizedSpecular", "_LightDataAOStrengths", "_LightingShadowMaskStrengths", "_DetailTexBlendMode", "_MainTexRotation", "_LightingDetailShadowStrength", "_SourceMochieReflection", "_SourceMochieReflectionTint", "NormalMapMode", "HeightMapScale", "_BumpMapFlipGreen", "_DetailNormalMapFlipGreen", "_SourceBackfaceNormals", "_FlipBackfaceNormals", "_UDIMDiscardUV", "_UDIMDiscardMode", "_GlobalMaskMin", "_GlobalMaskMax", "_GlobalMaskInvert", "_GlobalMaskDistance", "_GlobalMaskDistanceMin", "_GlobalMaskDistanceMax", "_GlobalMaskModifiers", "_MainContrast", "_MainGrayscale", "_MainColorReplaceStrength", "_PBRMochiePackedMasksEnabled", "_SourceMochieSettings", "_SourceMochieSecond", "_SourceRimSettings", "_SourceRimColorSettings", "_PBRReflectionStrength", "_PBRMetallicMapsReflectionMaskChannel", "_PBRMetallicMapsSpecularMaskChannel", "_PBRReflectionMaskInvert", "_PBRSpecularMaskInvert", "_SourceDissolveParams", "_DissolveTextureColor", "_DissolveToEmissionStrength", "_SourceGlitter", "_GlitterFrequency", "_GlitterLayers", "_GlitterUV", "_GlitterMode", "_GlitterShape", "_GlitterAngleRange", "_GlitterContrast", "_GlitterBias", "_GlitterMinBrightness", "_GlitterCenterSize", "_GlitterRandomLocation", "_GlitterUseNormals", "_GlitterUseSurfaceColor", "_GlitterHideInShadow", "_GlitterScaleWithLighting", "_GlitterBlendType", "_GlitterMaskChannel", "_GlitterMaskInvert", "_GlitterUVPanning", "_SourceMatcap"];
        names.Add("_SourceClearCoat");
        names.Add("_SourceLayeredEmission");
        names.Add("_SourceGlitterVersion");
        names.Add("_GlitterDistanceLOD");
        names.AddRange(new[] { "_SourceFlipbook", "_SourceFlipbookLayout", "_SourceFlipbookSides",
            "_SourceFlipbookOptions", "_SourceFlipbookMotion", "_SourceFlipbookFlags", "_SourceFlipbookHue",
            "_FlipbookScaleOffset", "_FlipbookColor", "_FlipbookFPS", "_FlipbookFrameOffset",
            "_FlipbookManualFrameControl", "_FlipbookCurrentFrame", "_FlipbookStartFrame", "_FlipbookEndFrame",
            "_FlipbookCrossfadeEnabled", "_FlipbookCrossfadeRange", "_FlipbookEmissionStrength",
            "_FlipbookBlendType", "_FlipbookAlphaControlsFinalAlpha" });
        names.Add("_SourceClearCoatGsaa");
        names.Add("_SourceClearCoatReflection");
        names.Add("_SourceClearCoatMaps_ST");
        names.Add("_SourceClearCoatMapsPan");
        names.Add("_SourceClearCoatMapsUV");
        names.Add("_SourceLightMode");
        names.Add("_SourceCubeMap");
        names.Add("_SourceEnviroRim");
        names.Add("_RimEnviroMask_ST");
        names.Add("_RimEnviroMaskPan");
        names.Add("_RimEnviroMaskUV");
        names.Add("_SourceLightSettings");
        names.Add("_SourceAdditiveSettings");
        names.Add("_SourceAdditiveColor");
        names.Add("_SourceAdditiveGradient");
        names.Add("_SourceWrappedColor");
        names.Add("_Shadow2ndColor");
        names.Add("_Shadow3rdColor");
        names.Add("_ShadowBorderColor");
        names.Add("_SourceMultilayerBorders");
        names.Add("_SourceMultilayerBlurs");
        names.Add("_SourceMultilayerReceive");
        names.Add("_SourceMultilayerMix");
        names.Add("_SourceMultilayerOptions");
        names.Add("_SourceMultilayerFlat");
        names.Add("_SourceMultilayerFlatAxis");
        names.Add("_ShadowAOShift");
        names.Add("_ShadowAOShift2");
        HashSet<string> seen = new(names, StringComparer.Ordinal);
        foreach (string sampler in Samplers)
        {
            if (seen.Add(sampler + "_ST")) names.Add(sampler + "_ST");
            if (seen.Add(sampler + "Pan")) names.Add(sampler + "Pan");
            if (seen.Add(sampler + "UV")) names.Add(sampler + "UV");
        }
        for (int index = 0; index < 4; ++index)
        {
            for (int column = 0; column < 4; ++column)
                Add("_UDIMDiscardRow" + index + "_" + column);
            Add("_GlobalThemeColor" + index);
            Add("_GlobalThemeAdjust" + index);
            Add("_DecalUvMode" + index);
            Add("_DecalSlotModifiers" + index);
            Add("_DecalSlotFx" + index);
            Add("_DecalBlendParams" + index);
            Add("_MatcapSlotParams" + index);
            Add("_MatcapSlotBlend" + index);
            Add("_MatcapSlotExtra" + index);
            Add("_MatcapSlotTheme" + index);
            Add("_MatcapSlotColor" + index);
            Add("_MatcapSlotSource" + index);
            Add("_MatcapSlotMask" + index);
            Add("_MatcapSlotUvBlend" + index);
            Add("_MatcapSlotUvControl" + index);
            Add("_MatcapSlotHue" + index);
            Add("_EmissionSlotParams" + index);
            Add("_EmissionSlotColor" + index);
            Add("_EmissionSlotPan" + index);
            Add("_EmissionSlotUv" + index);
            Add("_EmissionSlotModifiers" + index);
            Add("_EmissionSlotSampling" + index);
        }
        for (int index = 0; index < 4; ++index)
        {
            string suffix = index == 0 ? string.Empty : index.ToString();
            Add("_DecalColor" + suffix);
            Add("_DecalPosition" + suffix);
            Add("_DecalScale" + suffix);
            Add("_DecalTexturePan" + suffix);
            Add("_DecalRotation" + suffix);
            Add("_DecalRotationSpeed" + suffix);
        }
        Add("_SourceAnisoSettings");
        Add("_SourceAnisoMix");
        for (int index = 0; index < 2; ++index)
        {
            Add("_SourceAnisoLayer" + index);
            Add("_SourceAnisoTint" + index);
        }
        return [.. names];

        void Add(string name) { if (seen.Add(name)) names.Add(name); }
    }

    private static MaterialBindingLayout Create(string name, MaterialBindingLayout template)
    {
        MaterialBindingField[] fields = new MaterialBindingField[Names.Length + 1];
        fields[0] = new("UberFeatureFlags", "vec4", "NativeUber.FeatureFlags", "vec4(0.0)");
        for (int index = 0; index < Names.Length; ++index)
            fields[index + 1] = new("UberValue" + Names[index], "vec4", "NativeUber." + Names[index], "vec4(0.0)");
        MaterialTextureBinding[] textures = new MaterialTextureBinding[Samplers.Length];
        for (int index = 0; index < textures.Length; ++index)
            textures[index] = new("UberTexture" + Samplers[index], "NativeUber." + Samplers[index],
                Samplers[index] == "_CubeMap" ? "Cube" : Samplers[index] == "_FlipbookTexArray" ? "2DArray" : "2D");
        return new(name, template.RenderPass, template.Outputs, fields, textures, usesFlags: false);
    }

    private static string[] BuildUdimCells()
    {
        string[] names = new string[16];
        for (int row = 0; row < 4; ++row)
            for (int column = 0; column < 4; ++column)
                names[row * 4 + column] = "_UDIMDiscardRow" + row + "_" + column;
        return names;
    }

    private static Dictionary<string, int> BuildIndices(string[] names)
    {
        Dictionary<string, int> indices = new(names.Length, StringComparer.Ordinal);
        for (int index = 0; index < names.Length; ++index)
            indices.Add(names[index], index);
        return indices;
    }

    private static void AppendLayoutHash(StringBuilder source, string name, string hash)
    {
        ulong value = 14695981039346656037ul;
        foreach (char letter in hash) { value ^= letter; value *= 1099511628211ul; }
        source.Append("#define ").Append(name).Append("_LO ")
            .Append(unchecked((uint)value)).AppendLine("u");
        source.Append("#define ").Append(name).Append("_HI ")
            .Append(unchecked((uint)(value >> 32))).AppendLine("u");
    }
}
