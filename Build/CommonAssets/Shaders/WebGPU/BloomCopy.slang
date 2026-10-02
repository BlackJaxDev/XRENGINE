// Raw HDR scene copy into bloom level zero. No bright-pass on this pass.
struct BloomCopyUniforms { float4 outputArea; }; // origin xy, extent zw in physical pixels.
[[vk::binding(0, 0)]] ConstantBuffer<BloomCopyUniforms> bloomCopy;
[[vk::binding(0, 1)]] Texture2D<float4> sourceTexture;
[[vk::binding(1, 1)]] SamplerState sourceSampler;
struct ScreenInput { [[vk::location(0)]] float3 position : POSITION; };
struct ScreenOutput { float4 position : SV_Position; };
[shader("vertex")]
ScreenOutput bloomCopyVertex(ScreenInput input)
{
    ScreenOutput output;
    output.position = float4(input.position.xy, 0.0, 1.0);
    return output;
}
[shader("fragment")]
float4 bloomCopyFragment(ScreenOutput input) : SV_Target0
{
    float2 uv = saturate((input.position.xy - bloomCopy.outputArea.xy) / max(bloomCopy.outputArea.zw, float2(1.0)));
    return sourceTexture.Sample(sourceSampler, uv);
}
