// The authored x4 scene has already been resolved to a single-sample linear HDR texture.
[[vk::binding(0, 1)]] Texture2D<float4> sourceTexture;
[[vk::binding(1, 1)]] SamplerState sourceSampler;

struct PresentInput
{
    [[vk::location(0)]] float3 position : POSITION;
};

struct PresentOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};

[shader("vertex")]
PresentOutput modularMsaaPresentVertex(PresentInput input)
{
    PresentOutput output;
    output.position = float4(input.position.xy, 0.0, 1.0);
    output.uv = input.position.xy * float2(0.5, -0.5) + 0.5;
    return output;
}

[shader("fragment")]
float4 modularMsaaPresentFragment(PresentOutput input) : SV_Target0
{
    return sourceTexture.Sample(sourceSampler, input.uv);
}
