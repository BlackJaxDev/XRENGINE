#define XRE_LOCAL_SHADOWS 1
#include "UberBaseLighting.slang"
#include "UberBaseRaster.slang"
#include "UberBasePbrEnvironment.slang"
#include "UberBaseForwardLighting.slang"

#ifdef XRE_UBER_BASE_ORDER_GATE
#include "AuthoredOrderGate.slang"
#endif

[shader("vertex")]
UberBaseVertexOutput uberBaseVertex(UberBaseVertexInput input)
{
    UberBaseVertexOutput output = uberBaseVertexCore(input);
#ifdef XRE_UBER_BASE_ORDER_GATE
    output.position = authoredOrderPosition(output.position);
#endif
    return output;
}

[shader("fragment")]
float4 uberBaseFragment(UberBaseVertexOutput input, bool frontFace : SV_IsFrontFace) : SV_Target0
{
    UberBaseMaterialParameters material = uberBaseResolveMaterial();
    UberBaseMesh mesh = uberBaseMesh(input, frontFace);
    mesh.worldNormal = uberBaseSampleNormal(material, mesh, XRE_UBER_BASE_FEATURES, uberBaseTime());
    float4 base = uberBaseSampleColor(material, mesh, uberBaseTime());
    float alpha = uberBaseSampleAlpha(material, mesh, base.a, XRE_UBER_BASE_FEATURES, uberBaseTime());
    if (!uberBaseCovered(material, alpha)) discard;
    float3 rms = uberBaseSampleRms(material, mesh, XRE_UBER_BASE_FEATURES, uberBaseTime());
    float3 color = uberBaseForwardLighting(mesh, base.rgb, rms, input.position.xy, uberBasePipelineFlags());
    color += uberBaseSampleEmission(material, mesh, XRE_UBER_BASE_FEATURES, uberBaseTime());
    return float4(color, uberBaseOutputAlpha(material, alpha));
}
