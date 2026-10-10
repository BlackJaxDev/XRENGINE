// Consume the pinned shared forward lighting/shadow implementation unchanged.
#define XRE_LOCAL_SHADOWS 1
#define standardLitVertex sharedLitColorVertex
#define standardLitFragment sharedLitColorFragment
#include "StandardLitColorDirectionalShadow.slang"
#undef standardLitVertex
#undef standardLitFragment
#include "AuthoredTexturedSampling.slang"
#include "AuthoredTexturedVertex.slang"
[shader("vertex")]
AuthoredTexturedVertexOutput authoredTexturedForwardVertex(AuthoredTexturedVertexInput input)
{
    return authoredTexturedVertex(input);
}
[shader("fragment")]
float4 standardLitFragment(AuthoredTexturedVertexOutput input) : SV_Target0
{
    float4 texColor = authoredTexturedColor(input.uv);
    float3 normal = authoredTexturedSurfaceNormal(input.uv, input.worldPosition,
        input.worldNormal, input.worldTangent, input.worldBitangent);
    float3 viewDirection = safeNormalize(view.cameraPosition.xyz - input.worldPosition);
    float3 albedo = texColor.rgb;
    float4 surface = material.roughnessMetallicSpecularEmission;
    if (authoredTexturedFeature(2))
        surface.z *= specularTexture.Sample(specularSampler, input.uv).r;
    float3 rms = float3(saturate(surface.x), saturate(surface.y), max(surface.z, 0.0));
    float3 color = float3(0.0);
    ADD_DIRECTIONAL(0)
    ADD_DIRECTIONAL(1)
    ADD_DIRECTIONAL(2)
    ADD_DIRECTIONAL(3)
    ADD_POINT(0)
    ADD_POINT(1)
    ADD_POINT(2)
    ADD_POINT(3)
    ADD_POINT(4)
    ADD_POINT(5)
    ADD_POINT(6)
    ADD_POINT(7)
    ADD_SPOT(0)
    ADD_SPOT(1)
    ADD_SPOT(2)
    ADD_SPOT(3)
    ADD_SPOT(4)
    ADD_SPOT(5)
    ADD_SPOT(6)
    ADD_SPOT(7)
    float3 ambient = lighting.globalAmbient.xyz;
    float ambientMax = max(ambient.x, max(ambient.y, ambient.z));
    ambient = ambientMax <= 0.0001 ? float3(0.08) : ambient * max(1.0, 0.08 / ambientMax);
    ambient *= ambientVisibility(input.position.xy, albedo);
    float3 shaded = color + ambient * albedo + albedo * surface.w;
    authoredTexturedCoverage(texColor.a);
    return float4(shaded, texColor.a);
}

#ifndef XRE_AUTHORED_TEXTURED_ORDER_GATE
[shader("vertex")]
AuthoredTexturedVertexOutput standardLitVertex(AuthoredTexturedVertexInput input)
{
    return authoredTexturedForwardVertex(input);
}
#endif
