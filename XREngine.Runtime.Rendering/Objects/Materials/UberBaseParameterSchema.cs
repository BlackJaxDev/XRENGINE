using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering;

/// <summary>Canonical Uber base numeric ABI shared by raster parameters and the browser companion.</summary>
public static class UberBaseParameterSchema
{
    public const int ByteSize = 496;
    public const int MemberCount = 69;
    private static readonly ShaderAbiMemberContract[] Fields =
    [
        new("_Color_0", "_Color", 0, 16, "vec4<f32>"),
        new("_MainTex_ST_0", "_MainTex_ST", 16, 16, "vec4<f32>"),
        new("_MainTexPan_0", "_MainTexPan", 32, 8, "vec2<f32>"),
        new("_MainTexUV_0", "_MainTexUV", 40, 4, "i32"),
        new("_MainVertexColoringEnabled_0", "_MainVertexColoringEnabled", 44, 4, "f32"),
        new("_MainVertexColoringLinearSpace_0", "_MainVertexColoringLinearSpace", 48, 4, "f32"),
        new("_MainVertexColoring_0", "_MainVertexColoring", 52, 4, "f32"),
        new("_MainUseVertexColorAlpha_0", "_MainUseVertexColorAlpha", 56, 4, "f32"),
        new("_AlphaMod_0", "_AlphaMod", 60, 4, "f32"),
        new("_AlphaMask_ST_0", "_AlphaMask_ST", 64, 16, "vec4<f32>"),
        new("_AlphaMaskPan_0", "_AlphaMaskPan", 80, 8, "vec2<f32>"),
        new("_AlphaMaskUV_0", "_AlphaMaskUV", 88, 4, "i32"),
        new("_MainAlphaMaskMode_0", "_MainAlphaMaskMode", 92, 4, "i32"),
        new("_AlphaMaskBlendStrength_0", "_AlphaMaskBlendStrength", 96, 4, "f32"),
        new("_AlphaMaskValue_0", "_AlphaMaskValue", 100, 4, "f32"),
        new("_AlphaMaskInvert_0", "_AlphaMaskInvert", 104, 4, "f32"),
        new("_Cutoff_0", "_Cutoff", 108, 4, "f32"),
        new("_Mode_0", "_Mode", 112, 4, "i32"),
        new("_AlphaForceOpaque_0", "_AlphaForceOpaque", 116, 4, "f32"),
        new("U_SpecularSmoothness_0", "_SpecularSmoothness", 120, 4, "f32"),
        new("U_SpecularStrength_0", "_SpecularStrength", 124, 4, "f32"),
        new("_BumpMap_ST_0", "_BumpMap_ST", 128, 16, "vec4<f32>"),
        new("_BumpMapPan_0", "_BumpMapPan", 144, 8, "vec2<f32>"),
        new("_BumpMapUV_0", "_BumpMapUV", 152, 4, "i32"),
        new("_BumpScale_0", "_BumpScale", 156, 4, "f32"),
        new("NormalMapMode_0", "NormalMapMode", 160, 4, "i32"),
        new("HeightMapScale_0", "HeightMapScale", 164, 4, "f32"),
        new("_PBRMetallicMapInvert_0", "_PBRMetallicMapInvert", 168, 4, "f32"),
        new("_PBRMetallicMultiplier_0", "_PBRMetallicMultiplier", 172, 4, "f32"),
        new("_PBRMetallicMaps_ST_0", "_PBRMetallicMaps_ST", 176, 16, "vec4<f32>"),
        new("_PBRMetallicMapsPan_0", "_PBRMetallicMapsPan", 192, 8, "vec2<f32>"),
        new("_PBRMetallicMapsUV_0", "_PBRMetallicMapsUV", 200, 4, "i32"),
        new("_PBRMetallicMapsMetallicChannel_0", "_PBRMetallicMapsMetallicChannel", 204, 4, "i32"),
        new("_PBRSmoothnessMaps_ST_0", "_PBRSmoothnessMaps_ST", 208, 16, "vec4<f32>"),
        new("_PBRSmoothnessMapsPan_0", "_PBRSmoothnessMapsPan", 224, 8, "vec2<f32>"),
        new("_PBRSmoothnessMapsUV_0", "_PBRSmoothnessMapsUV", 232, 4, "i32"),
        new("_PBRSmoothnessMapsChannel_0", "_PBRSmoothnessMapsChannel", 236, 4, "i32"),
        new("_PBRSmoothnessMapInvert_0", "_PBRSmoothnessMapInvert", 240, 4, "f32"),
        new("_PBRRoughnessMultiplier_0", "_PBRRoughnessMultiplier", 244, 4, "f32"),
        new("U_SpecularType_0", "_SpecularType", 248, 4, "i32"),
        new("U_StylizedSpecular_0", "_StylizedSpecular", 252, 4, "f32"),
        new("U_SpecularMap_ST_0", "_SpecularMap_ST", 256, 16, "vec4<f32>"),
        new("U_SpecularTint_0", "_SpecularTint", 272, 16, "vec4<f32>"),
        new("_EmissionMap_ST_0", "_EmissionMap_ST", 288, 16, "vec4<f32>"),
        new("_EmissionMapPan_0", "_EmissionMapPan", 304, 8, "vec2<f32>"),
        new("_EmissionMapUV_0", "_EmissionMapUV", 312, 4, "i32"),
        new("_EmissionStrength_0", "_EmissionStrength", 316, 4, "f32"),
        new("_EmissionColor_0", "_EmissionColor", 320, 16, "vec4<f32>"),
        new("_EmissionScrollingSpeed_0", "_EmissionScrollingSpeed", 336, 8, "vec2<f32>"),
        new("_EmissionScrollingEnabled_0", "_EmissionScrollingEnabled", 344, 4, "f32"),
        new("_EmissionScrollingVertexColor_0", "_EmissionScrollingVertexColor", 348, 4, "f32"),
        new("_BackfaceNormalControl_0", "_BackfaceNormalControl", 352, 4, "i32"),
        new("_FlipBackfaceNormals_0", "_FlipBackfaceNormals", 356, 4, "i32"),
        new("_AlphaClipAllModes_0", "_AlphaClipAllModes", 360, 4, "f32"),
        new("_WorldAmbientOcclusionStrength_0", "_WorldAmbientOcclusionStrength", 364, 4, "f32"),
        new("_BumpMapFlipGreen_0", "_BumpMapFlipGreen", 368, 4, "i32"),
        new("_LightModel_0", "_LightModel", 372, 4, "i32"),
        new("_PBRPackedMasksEnabled_0", "_PBRPackedMasksEnabled", 376, 4, "i32"),
        new("_PBRMetallicMapsReflectionMaskChannel_0", "_PBRMetallicMapsReflectionMaskChannel", 380, 4, "i32"),
        new("_PBRMetallicMapsSpecularMaskChannel_0", "_PBRMetallicMapsSpecularMaskChannel", 384, 4, "i32"),
        new("_PBRReflectionStrength_0", "_PBRReflectionStrength", 388, 4, "f32"),
        new("_PBRReflectionMaskInvert_0", "_PBRReflectionMaskInvert", 392, 4, "f32"),
        new("_PBRSpecularMaskInvert_0", "_PBRSpecularMaskInvert", 396, 4, "f32"),
        new("U_SpecularAASettings_0", "_SpecularAASettings", 400, 16, "vec4<f32>"),
        new("U_SecondSpecularSettings_0", "_SecondSpecularSettings", 416, 16, "vec4<f32>"),
        new("_ReflectionResponse_0", "_ReflectionResponse", 432, 16, "vec4<f32>"),
        new("_ReflectionTint_0", "_ReflectionTint", 448, 16, "vec4<f32>"),
        new("_AdditiveLightSettings_0", "_AdditiveLightSettings", 464, 16, "vec4<f32>"),
        new("_AdditiveLightColor_0", "_AdditiveLightColor", 480, 16, "vec4<f32>"),
    ];

    /// <summary>
    /// Index of the first imported source control. A material that has no
    /// parameter for an animated control at or after this index receives zero,
    /// as an unset desktop uniform reads zero. Earlier members stay required.
    /// </summary>
    public const int FirstSourceControlIndex = 51;

    public static ReadOnlySpan<ShaderAbiMemberContract> Members => Fields;

    /// <summary>
    /// Returns true when the member is part of the selected feature set. Inactive
    /// members keep zero bytes, and the binding does not read their parameters.
    /// The feature bits are those of UberBaseMaterialProfile: 1 normal map,
    /// 2 alpha masks, 4 advanced specular, and 8 emission. The shader cooker
    /// compiles this file without that type.
    /// </summary>
    public static bool IsActive(int index, uint features)
        => index switch
        {
            < 0 or >= MemberCount => false,
            >= 9 and <= 15 => (features & 2u) != 0,
            >= 21 and <= 26 or 55 => (features & 1u) != 0,
            >= 27 and <= 42 or >= 56 => (features & 4u) != 0,
            >= 43 and <= 50 => (features & 8u) != 0,
            _ => true,
        };

    /// <summary>Returns true when an absent material parameter for this member reads as zero.</summary>
    public static bool ReadsZeroWhenAbsent(int index)
        => index is >= FirstSourceControlIndex and < MemberCount;

    public static int IndexOf(string? name)
    {
        for (int index = 0; index < Fields.Length; index++)
            if (Fields[index].ProviderName == name) return index;
        return -1;
    }
}
