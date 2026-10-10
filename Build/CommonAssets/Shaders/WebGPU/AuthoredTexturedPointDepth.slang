// Alpha families retain strict source coverage; other families never clip.
struct AuthoredDepthView { float4x4 viewProjection; };
struct AuthoredDepthObject { float4x4 modelMatrix; };
[[vk::binding(0, 0)]] ConstantBuffer<AuthoredDepthView> view;
[[vk::binding(1, 0)]] ConstantBuffer<AuthoredDepthObject> object;
#define XRE_AUTHORED_TEXTURED_DEPTH_ONLY 1
#include "AuthoredTexturedSampling.slang"
struct PointDepthLight { float3 lightPosition; float farPlaneDistance; };
[[vk::binding(2, 0)]] ConstantBuffer<PointDepthLight> light;
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
    [[vk::location(1)]] float3 worldPosition : TEXCOORD1;
};
[shader("vertex")]
AuthoredDepthOutput pointShadowDepthVertex(AuthoredDepthInput input)
{
    AuthoredDepthOutput output;
    float4 world = mul(object.modelMatrix, float4(input.position, 1.0));
    output.position = mul(view.viewProjection, world);
    // Preserve the existing shared OpenGL cube camera face axes.
    output.position.y = -output.position.y;
    output.worldPosition = world.xyz;
    output.uv = input.uv;
    return output;
}
[shader("fragment")]
float pointShadowDepthFragment(AuthoredDepthOutput input) : SV_Target0
{
    authoredTexturedCoverage(authoredTexturedColor(input.uv).a);
    return saturate(length(input.worldPosition - light.lightPosition) / max(light.farPlaneDistance, 0.0001));
}
