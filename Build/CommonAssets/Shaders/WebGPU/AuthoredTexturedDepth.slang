// Alpha families retain strict source coverage; other families never clip.
struct AuthoredDepthView { float4x4 viewProjection; };
struct AuthoredDepthObject { float4x4 modelMatrix; };
[[vk::binding(0, 0)]] ConstantBuffer<AuthoredDepthView> view;
[[vk::binding(1, 0)]] ConstantBuffer<AuthoredDepthObject> object;
#define XRE_AUTHORED_TEXTURED_DEPTH_ONLY 1
#include "AuthoredTexturedSampling.slang"
struct AuthoredDepthInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
    [[vk::location(2)]] float2 uv : TEXCOORD0;
};
struct AuthoredDepthOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};
[shader("vertex")]
AuthoredDepthOutput coverageDepthVertex(AuthoredDepthInput input)
{
    AuthoredDepthOutput output;
    float4 world = mul(object.modelMatrix, float4(input.position, 1.0));
    output.position = mul(view.viewProjection, world);
    output.uv = input.uv;
    return output;
}
[shader("fragment")]
void coverageDepthFragment(AuthoredDepthOutput input)
{
    authoredTexturedCoverage(authoredTexturedColor(input.uv).a);
}
