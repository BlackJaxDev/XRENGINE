// Desktop 9-tap tent upsample. The render pass blends ONE,ONE into the
// destination downsample level; the shader must never pre-read that target.
struct BloomUpsampleUniforms
{
    float4 outputArea;   // origin xy, extent zw in physical pixels.
    float4 sourceTexel;  // source inverse extent xy, Radius z, Scatter w.
};
[[vk::binding(0, 0)]] ConstantBuffer<BloomUpsampleUniforms> bloomUpsample;
[[vk::binding(0, 1)]] Texture2D<float4> sourceTexture;
[[vk::binding(1, 1)]] SamplerState sourceSampler;
struct ScreenInput { [[vk::location(0)]] float3 position : POSITION; };
struct ScreenOutput { float4 position : SV_Position; };
[shader("vertex")]
ScreenOutput bloomUpsampleVertex(ScreenInput input)
{
    ScreenOutput output;
    output.position = float4(input.position.xy, 0.0, 1.0);
    return output;
}
float3 sampleSource(float2 uv)
{
    return sourceTexture.SampleLevel(sourceSampler, uv, 0.0).rgb;
}
[shader("fragment")]
float4 bloomUpsampleFragment(ScreenOutput input) : SV_Target0
{
    float2 uv = saturate((input.position.xy - bloomUpsample.outputArea.xy) / max(bloomUpsample.outputArea.zw, float2(1.0)));
    float2 texel = bloomUpsample.sourceTexel.xy * bloomUpsample.sourceTexel.z;
    float3 result = sampleSource(uv + texel * float2(-1.0, -1.0))
                  + sampleSource(uv + texel * float2( 0.0, -1.0)) * 2.0
                  + sampleSource(uv + texel * float2( 1.0, -1.0))
                  + sampleSource(uv + texel * float2(-1.0,  0.0)) * 2.0
                  + sampleSource(uv) * 4.0
                  + sampleSource(uv + texel * float2( 1.0,  0.0)) * 2.0
                  + sampleSource(uv + texel * float2(-1.0,  1.0))
                  + sampleSource(uv + texel * float2( 0.0,  1.0)) * 2.0
                  + sampleSource(uv + texel * float2( 1.0,  1.0));
    return float4(max(result / 16.0, float3(0.0)) * bloomUpsample.sourceTexel.w, 1.0);
}
