// Browser SDR presentation of the linear HDR forward target. The Mobius curve
// matches Snippets/ToneMapping.glsl. Gamma encoding is explicit because the
// browser presentation attachment uses a non-sRGB format.
struct TonemapUniforms
{
    // Fallback exposure, display gamma, Mobius transition, GPU-exposure ready flag.
    float4 parameters;
};

[[vk::binding(0, 0)]] ConstantBuffer<TonemapUniforms> tonemap;
[[vk::binding(0, 1)]] Texture2D<float4> sourceTexture;
[[vk::binding(1, 1)]] SamplerState sourceSampler;
[[vk::binding(2, 1)]] Texture2D<float4> AutoExposureTex;

struct TonemapInput
{
    [[vk::location(0)]] float3 position : POSITION;
};

struct TonemapOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};

[shader("vertex")]
TonemapOutput tonemapVertex(TonemapInput input)
{
    TonemapOutput output;
    output.position = float4(input.position.xy, 0.0, 1.0);
    output.uv = input.position.xy * float2(0.5, -0.5) + 0.5;
    return output;
}

[shader("fragment")]
float4 tonemapFragment(TonemapOutput input) : SV_Target0
{
    float4 source = sourceTexture.Sample(sourceSampler, input.uv);
    float exposure = tonemap.parameters.x;
    if (tonemap.parameters.w > 0.5)
        exposure = AutoExposureTex.Load(int3(0, 0, 0)).r;
    float3 exposed = max(source.xyz * max(exposure, 0.0), float3(0.0));
    float transition = max(tonemap.parameters.z, 0.0001);
    float3 mapped = exposed * (transition + 1.0) / (exposed + transition);
    float inverseGamma = 1.0 / max(tonemap.parameters.y, 0.0001);
    return float4(pow(saturate(mapped), float3(inverseGamma)), source.w);
}
