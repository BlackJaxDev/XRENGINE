// Exact ordinary unlit fragment family. Kind: 1 color, 2 RGBA, 3 opaque RGB,
// 4 alpha-tested RGBA, 5 layer-zero RGBA array. Pass: 1 forward, 2 normal,
// 3 directional depth, 4 radial point depth, 5 projected spot depth.
#ifndef UNLIT_KIND
#error UNLIT_KIND is required
#endif
#ifndef UNLIT_PASS
#error UNLIT_PASS is required
#endif
struct UnlitView { float4x4 viewProjection; };
struct UnlitObject { float4x4 modelMatrix; float4x4 normalMatrix; };
[[vk::binding(0, 0)]] ConstantBuffer<UnlitView> view;
[[vk::binding(1, 0)]] ConstantBuffer<UnlitObject> object;
#if UNLIT_KIND == 1 && UNLIT_PASS == 1
struct UnlitMaterial { float4 matColor; };
[[vk::binding(0, 1)]] ConstantBuffer<UnlitMaterial> material;
#elif UNLIT_KIND == 4
struct UnlitMaterial { float4 alphaControls; };
[[vk::binding(0, 1)]] ConstantBuffer<UnlitMaterial> material;
#endif
#if (UNLIT_PASS == 1 || UNLIT_KIND == 4) && UNLIT_KIND == 5
[[vk::binding(1, 1)]] Texture2DArray<float4> texture0;
[[vk::binding(2, 1)]] SamplerState texture0Sampler;
#elif (UNLIT_PASS == 1 || UNLIT_KIND == 4) && UNLIT_KIND != 1
[[vk::binding(1, 1)]] Texture2D<float4> texture0;
[[vk::binding(2, 1)]] SamplerState texture0Sampler;
#endif
#if UNLIT_PASS == 4
struct UnlitPointLight { float3 lightPosition; float farPlaneDistance; };
[[vk::binding(2, 0)]] ConstantBuffer<UnlitPointLight> light;
#endif
struct UnlitVertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
#if UNLIT_KIND != 1
    [[vk::location(2)]] float2 uv : TEXCOORD0;
#endif
};
struct UnlitVertexOutput
{
    float4 position : SV_Position;
#if UNLIT_PASS == 2
    [[vk::location(0)]] float3 worldNormal : TEXCOORD0;
#endif
#if UNLIT_KIND != 1
    [[vk::location(1)]] float2 uv : TEXCOORD1;
#endif
#if UNLIT_PASS == 4
    [[vk::location(2)]] float3 worldPosition : TEXCOORD2;
#endif
};
[shader("vertex")]
UnlitVertexOutput unlitVertex(UnlitVertexInput input)
{
    UnlitVertexOutput output;
    float4 world = mul(object.modelMatrix, float4(input.position, 1.0));
    output.position = mul(view.viewProjection, world);
#if UNLIT_PASS == 4
    output.position.y = -output.position.y;
    output.worldPosition = world.xyz;
#endif
#if UNLIT_PASS == 2
    output.worldNormal = mul(object.normalMatrix, float4(input.normal, 0.0)).xyz;
#endif
#if UNLIT_KIND != 1
    output.uv = input.uv;
#endif
    return output;
}
#if UNLIT_PASS == 1 || UNLIT_KIND == 4
float4 unlitColor(UnlitVertexOutput input)
{
#if UNLIT_KIND == 1
    return material.matColor;
#elif UNLIT_KIND == 5
    return texture0.Sample(texture0Sampler, float3(input.uv, 0.0));
#else
    float4 color = texture0.Sample(texture0Sampler, input.uv);
#if UNLIT_KIND == 3
    color.a = 1.0;
#elif UNLIT_KIND == 4
    if (color.a < material.alphaControls.x)
        discard;
#endif
    return color;
#endif
}
#endif
#if UNLIT_PASS == 1
[shader("fragment")]
float4 unlitFragment(UnlitVertexOutput input) : SV_Target0
{
    return unlitColor(input);
}
#elif UNLIT_PASS == 2
[shader("fragment")]
float4 unlitNormalFragment(UnlitVertexOutput input) : SV_Target0
{
#if UNLIT_KIND == 4
    unlitColor(input);
#endif
    float3 normal = input.worldNormal * rsqrt(max(dot(input.worldNormal, input.worldNormal), 1e-20));
    float3 n = normal / max(abs(normal.x) + abs(normal.y) + abs(normal.z), 1e-6);
    float2 oct = n.xy;
    if (n.z < 0.0)
        oct = (1.0 - abs(oct.yx)) * float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return float4(oct * 0.5 + 0.5, 0.0, 1.0);
}
#elif UNLIT_PASS == 3
[shader("fragment")]
void unlitDepthFragment(UnlitVertexOutput input)
{
    unlitColor(input);
}
#elif UNLIT_PASS == 4
[shader("fragment")]
float unlitPointDepthFragment(UnlitVertexOutput input) : SV_Target0
{
    unlitColor(input);
    return saturate(length(input.worldPosition - light.lightPosition) / max(light.farPlaneDistance, 0.0001));
}
#elif UNLIT_PASS == 5
[shader("fragment")]
float unlitSpotDepthFragment(UnlitVertexOutput input) : SV_Target0
{
    unlitColor(input);
    return input.position.z;
}
#endif
