// Fixed physical role slots. Flags are validated against the exact cooked family.
struct AuthoredTexturedControls { float4 textureFlagsCutoffNormalModeHeightScale; };
[[vk::binding(9, 1)]] ConstantBuffer<AuthoredTexturedControls> authoredControls;
[[vk::binding(1, 1)]] Texture2D<float4> baseColorTexture;
[[vk::binding(2, 1)]] SamplerState baseColorSampler;
[[vk::binding(3, 1)]] Texture2D<float4> normalTexture;
[[vk::binding(4, 1)]] SamplerState normalSampler;
[[vk::binding(5, 1)]] Texture2D<float4> specularTexture;
[[vk::binding(6, 1)]] SamplerState specularSampler;
[[vk::binding(7, 1)]] Texture2D<float4> opacityTexture;
[[vk::binding(8, 1)]] SamplerState opacitySampler;
bool authoredTexturedFeature(int flag)
{
    return (int(authoredControls.textureFlagsCutoffNormalModeHeightScale.x) & flag) != 0;
}
float4 authoredTexturedColor(float2 uv)
{
    float4 color = baseColorTexture.Sample(baseColorSampler, uv);
    if (authoredTexturedFeature(4))
        color.a *= opacityTexture.Sample(opacitySampler, uv).r;
    return color;
}
void authoredTexturedCoverage(float alpha)
{
    if (authoredTexturedFeature(4) && alpha < authoredControls.textureFlagsCutoffNormalModeHeightScale.y)
        discard;
}
#ifndef XRE_AUTHORED_TEXTURED_DEPTH_ONLY
#include "AuthoredTexturedNormalMapping.slang"
float3 authoredTexturedSurfaceNormal(float2 uv, float3 position, float3 normal,
    float3 tangent, float3 bitangent)
{
    // Derivatives remain uniform even when a source basis is invalid.
    float3 positionDx = ddx(position), positionDy = ddy(position);
    float2 uvDx = ddx(uv), uvDy = ddy(uv);
    if (!authoredTexturedFeature(1))
        return normalize(normal);
    float3 sampledNormal;
    if (int(authoredControls.textureFlagsCutoffNormalModeHeightScale.z) == 1)
    {
        uint width, height;
        normalTexture.GetDimensions(width, height);
        float2 texel = 1.0 / float2(width, height);
        float tl = normalTexture.Sample(normalSampler, uv + float2(-texel.x, -texel.y)).r;
        float t = normalTexture.Sample(normalSampler, uv + float2(0.0, -texel.y)).r;
        float tr = normalTexture.Sample(normalSampler, uv + float2(texel.x, -texel.y)).r;
        float l = normalTexture.Sample(normalSampler, uv + float2(-texel.x, 0.0)).r;
        float r = normalTexture.Sample(normalSampler, uv + float2(texel.x, 0.0)).r;
        float bl = normalTexture.Sample(normalSampler, uv + float2(-texel.x, texel.y)).r;
        float b = normalTexture.Sample(normalSampler, uv + float2(0.0, texel.y)).r;
        float br = normalTexture.Sample(normalSampler, uv + float2(texel.x, texel.y)).r;
        sampledNormal = authoredTexturedSobelNormal(tl, t, tr, l, r, bl, b, br,
            authoredControls.textureFlagsCutoffNormalModeHeightScale.w);
    }
    else
        sampledNormal = authoredTexturedRgbNormal(normalTexture.Sample(normalSampler, uv).rgb);
    return authoredTexturedMappedNormal(sampledNormal, normal, tangent, bitangent,
        positionDx, positionDy, uvDx, uvDy);
}
#endif
