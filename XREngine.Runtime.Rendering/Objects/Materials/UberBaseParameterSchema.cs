using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering;

/// <summary>Canonical Uber base numeric ABI shared by raster parameters and the browser companion.</summary>
public static class UberBaseParameterSchema
{
    public const int ByteSize = 352;
    public const int MemberCount = 51;
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
    ];

    public static ReadOnlySpan<ShaderAbiMemberContract> Members => Fields;

    public static bool IsActive(int index, uint features)
        => (uint)index < MemberCount &&
            (index is < 9 or > 15 || (features & 2u) != 0) &&
            (index is < 21 or > 26 || (features & 1u) != 0) &&
            (index is < 27 or > 42 || (features & 4u) != 0) &&
            (index < 43 || (features & 8u) != 0);

    public static int IndexOf(string? name)
    {
        for (int index = 0; index < Fields.Length; index++)
            if (Fields[index].ProviderName == name) return index;
        return -1;
    }
}
