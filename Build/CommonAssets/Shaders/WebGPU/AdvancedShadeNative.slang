// Advanced native opaque compute shading over GPU-classified texture cohorts.
#include "AdvancedShadeContracts.slang"
#include "AdvancedShadeTextures.slang"
#include "AdvancedShadeEngineSurface.slang"
#include "AdvancedShadeReconstruction.slang"
#include "AdvancedShadeLighting.slang"

[[vk::binding(0, 1)]] Texture2D<uint2> VisibilityIdentity;
[[vk::binding(1, 1)]] Texture2D<uint> VisibilityMetadata;
[[vk::binding(2, 1)]] DepthTexture2D VisibilityDepth;
[[vk::binding(3, 1)]] Texture2D<float> AmbientOcclusion;
#if defined(XR_ADV_SURFACE_EXPORTS)
[[vk::binding(0, 2)]] [[vk::image_format("rgba16f")]] WTexture2D<float4> EmissionColor;
[[vk::binding(1, 2)]] [[vk::image_format("rgba16f")]] WTexture2D<float4> AlbedoOpacity;
[[vk::binding(2, 2)]] [[vk::image_format("rgba16f")]] WTexture2D<float4> Normal;
[[vk::binding(3, 2)]] [[vk::image_format("rgba16f")]] WTexture2D<float4> RMSE;
#else
[[vk::binding(0, 2)]] [[vk::image_format("rgba16f")]] WTexture2D<float4> HDRSceneColor;
[[vk::binding(1, 2)]] [[vk::image_format("rgba16f")]] WTexture2D<float4> Velocity;
[[vk::binding(2, 2)]] [[vk::image_format("r32f")]] WTexture2D<float> ReactiveMask;
[[vk::binding(3, 2)]] [[vk::image_format("r32ui")]] WTexture2D<uint> ShadingDiagnostics;
#endif

float2 EncodeNormal(float3 normal)
{
    float3 n = normal / max(abs(normal.x) + abs(normal.y) + abs(normal.z), 1.0e-6);
    float2 oct = n.xy;
    if (n.z < 0.0)
        oct = (1.0 - abs(oct.yx)) * float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return oct * 0.5 + 0.5;
}
void StoreResult(uint2 pixel, XRAdvancedOpaqueSampleResult result)
{
#if defined(XR_ADV_SURFACE_EXPORTS)
    EmissionColor.Store(pixel, float4(result.emittedRadiance, float(result.hasMaterialSurface)));
    AlbedoOpacity.Store(pixel, result.albedoOpacity);
    Normal.Store(pixel, float4(EncodeNormal(normalize(result.normal)), 0.0, 1.0));
    RMSE.Store(pixel, float4(result.roughness, result.metallic, result.rmse.zw));
#else
    HDRSceneColor.Store(pixel, float4(result.radiance, result.opacity));
    Velocity.Store(pixel, float4(result.velocity, 0.0, 0.0));
    ReactiveMask.Store(pixel, round(saturate(result.reactive) * 255.0) / 255.0);
    ShadingDiagnostics.Store(pixel, result.diagnostics);
#endif
}
[shader("compute")]
[numthreads(16, 16, 1)]
void advancedShadeNative(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID)
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
    uint2 identity = VisibilityIdentity.Load(int3(pixel, 0));
    if (identity.x == 0u || identity.x == XR_ADV_VIS_INVALID) return;
    uint metadata = VisibilityMetadata.Load(int3(pixel, 0));
    uint materialDense, kernelDense;
    bool resolved = XR_ADV_TryResolvePixelMaterial(identity, metadata, materialDense, kernelDense);
    // The reserved diagnostic cohort owns invalid pixels exclusively.
    bool diagnostic = Parameters.kernelIndex == XR_ADV_INVALID_DENSE_INDEX;
    bool mapped = resolved && materialDense < MaterialGroupCount() &&
        MaterialGroups[materialDense] < Parameters.cohortCount - 1u;
    if (diagnostic && mapped) return;
    if (!diagnostic && (!mapped || MaterialGroups[materialDense] != Parameters.cohortIndex)) return;
    if (!resolved && !diagnostic) return;
    if (!diagnostic && resolved && (kernelDense != Parameters.kernelIndex || ShadeTiles[tileWord + 2u] != kernelDense)) return;
    textureFailure = 0u;
    float ambientOcclusion = 1.0;
    if ((Parameters.flags & 4u) != 0u)
        ambientOcclusion = AmbientOcclusion.Load(int3(pixel, 0));
    XRAdvancedOpaqueSampleResult result;
    XR_ADV_EvaluateNativeOpaqueSample(pixel, viewIndex, identity, metadata,
        VisibilityDepth.Load(int3(pixel, 0)), float2(pixel) + 0.5,
        ambientOcclusion, false, ShadeTiles[tileWord + 3u], !diagnostic, result);
    if (diagnostic && resolved) textureFailure |= 1u << 17u;
    if (textureFailure != 0u)
    {
        result = XR_ADV_NeutralOpaqueSampleResult();
        result.radiance = float3(1.0, 0.0, 1.0);
        result.opacity = 1.0;
        result.reactive = 1.0;
        result.diagnostics = textureFailure;
    }
    StoreResult(pixel, result);
}
