struct UberBaseDepthView { float4x4 viewProjection; float4 cameraPosition; float RenderTime; };
struct UberBaseDepthObject { float4x4 modelMatrix; };
[[vk::binding(0, 0)]] ConstantBuffer<UberBaseDepthView> view;
[[vk::binding(1, 0)]] ConstantBuffer<UberBaseDepthObject> object;
#define XRE_UBER_BASE_DEPTH 1
#include "UberBaseRaster.slang"
#ifdef XRE_UBER_BASE_POINT_DEPTH
struct UberBasePointDepth { float3 lightPosition; float farPlaneDistance; };
[[vk::binding(3, 0)]] ConstantBuffer<UberBasePointDepth> pointDepth;
#endif

[shader("vertex")]
UberBaseVertexOutput uberBaseVertex(UberBaseVertexInput input)
{
    return uberBaseVertexCore(input);
}

// Coverage runs before all shadow and depth outputs. The canonical source
// deliberately keeps these passes on geometric normals, even with a bump map.
[shader("fragment")]
#ifdef XRE_UBER_BASE_DEPTH_ONLY
void uberBaseFragment(UberBaseVertexOutput input, bool frontFace : SV_IsFrontFace)
#else
float4 uberBaseFragment(UberBaseVertexOutput input, bool frontFace : SV_IsFrontFace) : SV_Target0
#endif
{
    UberBaseMaterialParameters material = uberBaseResolveMaterial();
    UberBaseMesh mesh = uberBaseMesh(input, frontFace);
    float4 base = uberBaseSampleColor(material, mesh, uberBaseTime());
    float alpha = uberBaseSampleAlpha(material, mesh, base.a, XRE_UBER_BASE_FEATURES, uberBaseTime());
    if (!uberBaseCovered(material, alpha)) discard;
#if defined(XRE_UBER_BASE_DEPTH_ONLY)
    return;
#elif defined(XRE_UBER_BASE_POINT_DEPTH)
    float depth = saturate(length(input.worldPosition - pointDepth.lightPosition) / max(pointDepth.farPlaneDistance, 0.0001));
    return float4(depth, 0.0, 0.0, 1.0);
#elif defined(XRE_UBER_BASE_DEPTH_NORMAL)
    float3 normal = mesh.vertexNormal / (abs(mesh.vertexNormal.x) + abs(mesh.vertexNormal.y) + abs(mesh.vertexNormal.z));
    float2 oct = normal.xy;
    if (normal.z < 0.0)
        oct = (1.0 - abs(oct.yx)) * float2(normal.x >= 0.0 ? 1.0 : -1.0, normal.y >= 0.0 ? 1.0 : -1.0);
    return float4(oct * 0.5 + 0.5, 0.0, 1.0);
#else
    return float4(input.position.z, 0.0, 0.0, 1.0);
#endif
}
