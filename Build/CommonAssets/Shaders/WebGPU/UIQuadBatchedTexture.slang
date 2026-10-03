// Textured screen UI uses the shared collector's row-major instance matrices.
#include "UIOutput.slang"
[[vk::binding(0, 1)]] StructuredBuffer<float4> quadTransforms;
[[vk::binding(1, 1)]] StructuredBuffer<float4> quadColors;
[[vk::binding(2, 1)]] StructuredBuffer<float4> quadBounds;
[[vk::binding(3, 1)]] StructuredBuffer<float4> quadUvs;
[[vk::binding(0, 2)]] Texture2D<float4> imageTexture;
[[vk::binding(1, 2)]] SamplerState imageSampler;

struct UIQuadTextureInput
{
    [[vk::location(0)]] float3 position : POSITION;
    uint instanceId : SV_InstanceID;
};
struct UIQuadTextureOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
    [[vk::location(1)]] nointerpolation float4 color : TEXCOORD1;
    [[vk::location(2)]] nointerpolation float4 bounds : TEXCOORD2;
};
[shader("vertex")]
UIQuadTextureOutput uiQuadTextureVertex(UIQuadTextureInput input)
{
    uint baseRow = input.instanceId * 4u;
    float4 local = float4(input.position, 1.0);
    float4 world = local.x * quadTransforms[baseRow] +
        local.y * quadTransforms[baseRow + 1u] +
        local.z * quadTransforms[baseRow + 2u] +
        local.w * quadTransforms[baseRow + 3u];
    float4 uvBounds = quadUvs[input.instanceId];
    UIQuadTextureOutput output;
    output.position = mul(view.viewProjection, world);
    output.uv = lerp(uvBounds.xy, uvBounds.zw, input.position.xy);
    output.color = quadColors[input.instanceId];
    output.bounds = quadBounds[input.instanceId];
    return output;
}
[shader("fragment")]
float4 uiQuadTextureFragment(UIQuadTextureOutput input) : SV_Target0
{
    if (any(isnan(input.bounds)))
        discard;
    float4 image = imageTexture.Sample(imageSampler, input.uv);
    if (!uiLinearOffscreenOutput())
        return image * input.color;
    float alpha = saturate(image.a * input.color.a);
    // The offscreen binding preserves linear images or selects the retained sRGB
    // view to decode display texels before filtering. Tint still needs decoding.
    return float4(image.rgb * uiDisplayToLinear(input.color.rgb) * alpha, alpha);
}
