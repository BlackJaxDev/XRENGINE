// Texture0 alpha times required Texture1 red owns coverage in every caster.
struct AlphaDepthView { float4x4 viewProjection; };
struct AlphaDepthObject { float4x4 modelMatrix; };
struct AlphaDepthMaterial { float4 baseColorOpacity; float4 coverage; };
[[vk::binding(0, 0)]] ConstantBuffer<AlphaDepthView> view;
[[vk::binding(1, 0)]] ConstantBuffer<AlphaDepthObject> object;
[[vk::binding(0, 1)]] ConstantBuffer<AlphaDepthMaterial> material;
#include "TexturedAlphaSampling.slang"
struct AlphaDepthInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
    [[vk::location(2)]] float2 uv : TEXCOORD0;
};
struct AlphaDepthOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};
[shader("vertex")]
AlphaDepthOutput spotShadowDepthVertex(AlphaDepthInput input)
{
    AlphaDepthOutput output;
    float4 world = mul(object.modelMatrix, float4(input.position, 1.0));
    output.position = mul(view.viewProjection, world);
    output.uv = input.uv;
    return output;
}
[shader("fragment")]
float spotShadowDepthFragment(AlphaDepthOutput input) : SV_Target0
{
    if (texturedAlphaSurface(input.uv).a < material.coverage.y)
        discard;
    return input.position.z;
}
