// Lookup-free SMAA blend-weight pass. Mirrors the embedded VPRC_SMAA shader.
struct SmaaBlendUniforms
{
    float ScreenWidth;
    float ScreenHeight;
    float2 ScreenOrigin;
    float2 TexelSize;
    int MaxSearchSteps;
    float EdgeSharpness;
};
[[vk::binding(0, 0)]] ConstantBuffer<SmaaBlendUniforms> smaa;
[[vk::binding(0, 1)]] Texture2D<float4> EdgeTexture;
[[vk::binding(1, 1)]] SamplerState EdgeTextureSampler;

struct ScreenInput { [[vk::location(0)]] float3 position : POSITION; };
struct ScreenOutput { float4 position : SV_Position; };
[shader("vertex")]
ScreenOutput outputVertex(ScreenInput input)
{
    ScreenOutput output;
    output.position = float4(input.position.xy, 0.0, 1.0);
    return output;
}
float4 SampleEdge(float2 uv)
{
    return EdgeTexture.SampleLevel(EdgeTextureSampler, uv, 0.0);
}
float SearchSpan(float2 uv, float2 direction, int channelIndex)
{
    float span = 0.0;
    float2 sampleUv = uv;
    for (int i = 0; i < 32; ++i)
    {
        if (i >= smaa.MaxSearchSteps)
            break;
        sampleUv += direction * smaa.TexelSize;
        float4 edge = SampleEdge(sampleUv);
        float value = channelIndex == 0 ? edge.r : edge.g;
        if (value < 0.5)
            break;
        span += 1.0;
    }
    return span;
}
[shader("fragment")]
float4 outputFragment(ScreenOutput input) : SV_Target0
{
    float2 uv = (input.position.xy - smaa.ScreenOrigin) / float2(smaa.ScreenWidth, smaa.ScreenHeight);
    float4 edge = SampleEdge(uv);
    float4 weights = float4(0.0);
    if (edge.r > 0.5)
    {
        float leftSpan = SearchSpan(uv, float2(-1.0, 0.0), 0);
        float rightSpan = SearchSpan(uv, float2(1.0, 0.0), 0);
        float totalSpan = leftSpan + rightSpan + 1.0;
        float spanWeight = 1.0 / sqrt(max(totalSpan, 1.0));
        float contrast = min(edge.b * smaa.EdgeSharpness, 1.0);
        weights.r = 0.5 * contrast * spanWeight;
    }
    if (edge.g > 0.5)
    {
        float upSpan = SearchSpan(uv, float2(0.0, -1.0), 1);
        float downSpan = SearchSpan(uv, float2(0.0, 1.0), 1);
        float totalSpan = upSpan + downSpan + 1.0;
        float spanWeight = 1.0 / sqrt(max(totalSpan, 1.0));
        float contrast = min(edge.a * smaa.EdgeSharpness, 1.0);
        weights.g = 0.5 * contrast * spanWeight;
    }
    return clamp(weights, 0.0, 1.0);
}
