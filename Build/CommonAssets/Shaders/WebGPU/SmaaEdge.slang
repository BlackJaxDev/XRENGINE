// Lookup-free SMAA edge pass. Mirrors the embedded VPRC_SMAA edge shader.
struct SmaaEdgeUniforms
{
    float ScreenWidth;
    float ScreenHeight;
    float2 ScreenOrigin;
    float2 TexelSize;
    float EdgeThreshold;
};
[[vk::binding(0, 0)]] ConstantBuffer<SmaaEdgeUniforms> smaa;
[[vk::binding(0, 1)]] Texture2D<float4> SourceTexture;
[[vk::binding(1, 1)]] SamplerState SourceTextureSampler;

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
float Luma(float3 color)
{
    return dot(color, float3(0.299, 0.587, 0.114));
}
[shader("fragment")]
float4 outputFragment(ScreenOutput input) : SV_Target0
{
    float2 uv = (input.position.xy - smaa.ScreenOrigin) / float2(smaa.ScreenWidth, smaa.ScreenHeight);
    float center = Luma(SampleSource(uv).rgb);
    // WebGPU texture V grows downward; the GLSL top neighbor grows upward.
    float top = Luma(SampleSource(uv - float2(0.0, smaa.TexelSize.y)).rgb);
    float left = Luma(SampleSource(uv - float2(smaa.TexelSize.x, 0.0)).rgb);
    float deltaH = abs(center - top);
    float deltaV = abs(center - left);
    float edgeH = deltaH >= smaa.EdgeThreshold ? 1.0 : 0.0;
    float edgeV = deltaV >= smaa.EdgeThreshold ? 1.0 : 0.0;
    return float4(edgeH, edgeV, deltaH, deltaV);
}
