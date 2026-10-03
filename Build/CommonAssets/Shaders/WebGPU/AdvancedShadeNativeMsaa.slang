// Each cohort owns disjoint raw samples; a separate pass resolves their f32 radiance.
#include "AdvancedShadeContracts.slang"
#include "AdvancedShadeTextures.slang"
#include "AdvancedShadeEngineSurface.slang"
#include "AdvancedShadeReconstruction.slang"
#include "AdvancedShadeLighting.slang"
#include "AdvancedMsaaVisibility.slang"

[[vk::binding(0, 1)]] Texture2DMS<uint4> RawVisibilityIdentity;
[[vk::binding(1, 1)]] Texture2DMS<uint4> RawVisibilityMetadataSelection;
[[vk::binding(2, 1)]] DepthTexture2DMS RawVisibilityDepth;
[[vk::binding(3, 1)]] Texture2D<float> AmbientOcclusion;
#if defined(XR_ADV_SURFACE_EXPORTS)
[[vk::binding(0, 2)]] [[vk::image_format("rgba16f")]] WTexture2D<float4> EmissionColor;
[[vk::binding(1, 2)]] [[vk::image_format("rgba16f")]] WTexture2D<float4> AlbedoOpacity;
[[vk::binding(2, 2)]] [[vk::image_format("rgba16f")]] WTexture2D<float4> Normal;
[[vk::binding(3, 2)]] [[vk::image_format("rgba16f")]] WTexture2D<float4> RMSE;
#else
[[vk::binding(0, 2)]] [[vk::image_format("rgba32f")]] WTexture2DArray<float4> SampleRadianceReactive;
[[vk::binding(1, 2)]] [[vk::image_format("rgba16f")]] WTexture2D<float4> Velocity;
[[vk::binding(2, 2)]] [[vk::image_format("r32ui")]] WTexture2D<uint> ShadingDiagnostics;
#endif

uint XR_WEB_SelectSidecarSample(uint2 pixel)
{
    bool reversedDepth = (FrozenView.flags & 2u) != 0u || FrozenView.depthParams.w != 0.0;
    float nearestDepth = reversedDepth ? 0.0 : 1.0;
    uint firstCovered = 0xffffffffu;
    uint nearestSample = 0xffffffffu;
    for (uint sampleIndex = 0u; sampleIndex < 4u; ++sampleIndex)
    {
        uint2 identity = XR_WEB_UnpackVisibilityPair(RawVisibilityIdentity.Load(int2(pixel), int(sampleIndex)));
        if (!XR_WEB_IsCoveredSample(identity)) continue;
        if (firstCovered == 0xffffffffu) firstCovered = sampleIndex;
        float depth = RawVisibilityDepth.Load(int2(pixel), int(sampleIndex));
        if (XR_WEB_IsValidSampleDepth(depth) &&
            (nearestSample == 0xffffffffu || XR_WEB_IsCloserSample(depth, nearestDepth, reversedDepth)))
        {
            nearestDepth = depth;
            nearestSample = sampleIndex;
        }
    }
    // Native desktop shading retains the first covered result if every depth is invalid.
    return nearestSample == 0xffffffffu ? firstCovered : nearestSample;
}

#if defined(XR_ADV_SURFACE_EXPORTS)
float2 XR_WEB_EncodeNormal(float3 normal)
{
    float3 n = normal / max(abs(normal.x) + abs(normal.y) + abs(normal.z), 1.0e-6);
    float2 oct = n.xy;
    if (n.z < 0.0)
        oct = (1.0 - abs(oct.yx)) * float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return oct * 0.5 + 0.5;
}
#endif

[shader("compute")]
[numthreads(16, 16, 1)]
void advancedShadeNativeMsaa(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID)
{
    if (Parameters.cohortIndex >= Parameters.cohortCount || Parameters.tileCapacity == 0u) return;
    uint tileIndex = group.x + group.y * 65535u;
    if (tileIndex >= min(ShadeCounts[Parameters.cohortIndex * 4u], Parameters.tileCapacity)) return;
    uint tileWord = (Parameters.cohortIndex * Parameters.tileCapacity + tileIndex) * 4u;
    if (tileWord > TileWordCount() || TileWordCount() - tileWord < 4u) return;
    uint packed = ShadeTiles[tileWord];
    uint viewIndex = ShadeTiles[tileWord + 1u];
    if (viewIndex != Parameters.viewIndex) return;
    uint2 pixel = uint2(packed & 65535u, packed >> 16u) * 16u + local.xy;
    if (any(pixel >= Parameters.screenExtent) || viewIndex >= Parameters.viewCount) return;
    float ambientOcclusion = 1.0;
    if ((Parameters.flags & 4u) != 0u)
        ambientOcclusion = AmbientOcclusion.Load(int3(pixel, 0));
    uint selectedSample = XR_WEB_SelectSidecarSample(pixel);
    for (uint sampleIndex = 0u; sampleIndex < 4u; ++sampleIndex)
    {
#if defined(XR_ADV_SURFACE_EXPORTS)
        if (sampleIndex != selectedSample) continue;
#endif
        uint2 identity = XR_WEB_UnpackVisibilityPair(RawVisibilityIdentity.Load(int2(pixel), int(sampleIndex)));
        if (!XR_WEB_IsCoveredSample(identity)) continue;
        uint metadata = XR_WEB_UnpackVisibilityPair(RawVisibilityMetadataSelection.Load(int2(pixel), int(sampleIndex))).x;
        uint materialDense, kernelDense;
        bool resolved = XR_ADV_TryResolvePixelMaterial(identity, metadata, materialDense, kernelDense);
        bool metadataMatchesView = ((metadata >> XR_ADV_VIS_VIEW_SHIFT) & 0xffu) == viewIndex;
        bool diagnostic = Parameters.kernelIndex == XR_ADV_INVALID_DENSE_INDEX;
        bool mapped = resolved && metadataMatchesView && materialDense < MaterialGroupCount() &&
            MaterialGroups[materialDense] < Parameters.cohortCount - 1u;
        if (diagnostic && mapped) continue;
        if (!diagnostic && (!mapped || MaterialGroups[materialDense] != Parameters.cohortIndex)) continue;
        if (!resolved && !diagnostic) continue;
        if (!diagnostic && resolved && (kernelDense != Parameters.kernelIndex || ShadeTiles[tileWord + 2u] != kernelDense)) continue;
        textureFailure = 0u;
        XRAdvancedOpaqueSampleResult result;
        XR_ADV_EvaluateNativeOpaqueSample(pixel, viewIndex, identity, metadata,
            RawVisibilityDepth.Load(int2(pixel), int(sampleIndex)), float2(pixel) + XR_WEB_SamplePosition(sampleIndex),
            ambientOcclusion, false, ShadeTiles[tileWord + 3u], !diagnostic, result);
        if (diagnostic && resolved && metadataMatchesView) textureFailure |= 1u << 17u;
        if (textureFailure != 0u)
        {
            result = XR_ADV_NeutralOpaqueSampleResult();
            result.radiance = float3(1.0, 0.0, 1.0);
            result.opacity = 1.0;
            result.reactive = 1.0;
            result.diagnostics = textureFailure;
        }
#if defined(XR_ADV_SURFACE_EXPORTS)
        EmissionColor.Store(pixel, float4(result.emittedRadiance, float(result.hasMaterialSurface)));
        AlbedoOpacity.Store(pixel, result.albedoOpacity);
        Normal.Store(pixel, float4(XR_WEB_EncodeNormal(normalize(result.normal)), 0.0, 1.0));
        RMSE.Store(pixel, float4(result.roughness, result.metallic, result.rmse.zw));
#else
        SampleRadianceReactive.Store(uint3(pixel, sampleIndex), float4(result.radiance, result.reactive));
        if (sampleIndex == selectedSample)
        {
            Velocity.Store(pixel, float4(result.velocity, 0.0, 0.0));
            ShadingDiagnostics.Store(pixel, result.diagnostics);
        }
#endif
    }
}
