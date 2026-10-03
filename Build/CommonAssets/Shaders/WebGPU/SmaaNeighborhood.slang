// Lookup-free SMAA neighborhood pass. Mirrors the embedded VPRC_SMAA shader.
struct SmaaNeighborhoodUniforms
{
    float ScreenWidth;
    float ScreenHeight;
    float2 ScreenOrigin;
    float2 TexelSize;
};
[[vk::binding(0, 0)]] ConstantBuffer<SmaaNeighborhoodUniforms> smaa;
[[vk::binding(0, 1)]] Texture2D<float4> SourceTexture;
[[vk::binding(1, 1)]] SamplerState SourceTextureSampler;
[[vk::binding(2, 1)]] Texture2D<float4> BlendTexture;
[[vk::binding(3, 1)]] SamplerState BlendTextureSampler;

struct ScreenInput { [[vk::location(0)]] float3 position : POSITION; };
struct ScreenOutput { float4 position : SV_Position; };
[shader("vertex")]
ScreenOutput outputVertex(ScreenInput input)
{
    ScreenOutput output;
    output.position = float4(input.position.xy, 0.0, 1.0);
    return output;
}
float4 SampleSource(float2 uv)
{
    return SourceTexture.SampleLevel(SourceTextureSampler, uv, 0.0);
}
float4 SampleBlend(float2 uv)
{
    return BlendTexture.SampleLevel(BlendTextureSampler, uv, 0.0);
}
[shader("fragment")]
float4 outputFragment(ScreenOutput input) : SV_Target0
{
    float2 uv = (input.position.xy - smaa.ScreenOrigin) / float2(smaa.ScreenWidth, smaa.ScreenHeight);
    float4 center = SampleSource(uv);
    float4 blend = SampleBlend(uv);
    float blendDown = SampleBlend(uv + float2(0.0, smaa.TexelSize.y)).r;
    float blendRight = SampleBlend(uv + float2(smaa.TexelSize.x, 0.0)).g;
    float bTop = blend.r;
    float bBottom = blendDown;
    float bLeft = blend.g;
    float bRight = blendRight;
    float total = bTop + bBottom + bLeft + bRight;
    if (total < 1e-5)
        return center;
    total = min(total, 1.0);
    float4 top = SampleSource(uv - float2(0.0, smaa.TexelSize.y));
    float4 bottom = SampleSource(uv + float2(0.0, smaa.TexelSize.y));
    float4 left = SampleSource(uv - float2(smaa.TexelSize.x, 0.0));
    float4 right = SampleSource(uv + float2(smaa.TexelSize.x, 0.0));
    float4 blended = top * bTop + bottom * bBottom + left * bLeft + right * bRight;
    return center * (1.0 - total) + blended;
}
