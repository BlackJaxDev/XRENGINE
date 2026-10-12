#define XRE_STANDARD_LIT_TEXTURE_NORMAL 1
#define XRE_TEXTURE_NORMAL_PREPASS 1
#include "StandardLitTextureSampling.slang"
struct TextureNormalView { float4x4 viewProjection; };
struct TextureNormalObject { float4x4 modelMatrix; float4x4 normalMatrix; };
[[vk::binding(0, 0)]] ConstantBuffer<TextureNormalView> view;
[[vk::binding(1, 0)]] ConstantBuffer<TextureNormalObject> object;
struct TextureNormalInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
    [[vk::location(2)]] float2 uv : TEXCOORD0;
    [[vk::location(3)]] float4 tangent : TANGENT;
};
struct TextureNormalOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 worldNormal : TEXCOORD0;
    [[vk::location(1)]] float3 worldTangent : TEXCOORD1;
    [[vk::location(2)]] float3 worldBitangent : TEXCOORD2;
    [[vk::location(3)]] float2 uv : TEXCOORD3;
};
[shader("vertex")]
TextureNormalOutput textureDepthNormalVertex(TextureNormalInput input)
{
    TextureNormalOutput output;
    output.position = mul(view.viewProjection, mul(object.modelMatrix, float4(input.position, 1.0)));
    output.worldNormal = normalize(mul(object.normalMatrix, float4(input.normal, 0.0)).xyz);
    output.worldTangent = normalize(mul(object.normalMatrix, float4(input.tangent.xyz, 0.0)).xyz);
    output.worldBitangent = normalize(mul(object.normalMatrix, float4(cross(input.normal, input.tangent.xyz) * input.tangent.w, 0.0)).xyz);
    output.uv = input.uv;
    return output;
}
[shader("fragment")]
float4 textureDepthNormalFragment(TextureNormalOutput input) : SV_Target0
{
    float3 normal = mappedTextureNormal(input.uv, input.worldNormal, input.worldTangent, input.worldBitangent);
    float3 n = normal / max(abs(normal.x) + abs(normal.y) + abs(normal.z), 1e-6);
    float2 oct = n.xy;
    if (n.z < 0.0)
        oct = (1.0 - abs(oct.yx)) * float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return float4(oct * 0.5 + 0.5, 0.0, 1.0);
}
