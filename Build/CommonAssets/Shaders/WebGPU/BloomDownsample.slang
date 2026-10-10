// Desktop BloomDownsample.fs 13-tap filter. Source is a separate one-level
// texture, so all texture sampling is LOD zero instead of SourceLOD.
struct BloomDownsampleUniforms
{
    float4 outputArea;             // origin xy, extent zw in physical pixels.
    float4 sourceTexelThreshold;   // source 1/width,1/height,threshold,softKnee.
    float4 controls;               // intensity,useKaris,useThreshold,debugSolid.
    float4 luminance;              // canonical RuntimeEngine luminance xyz.
};
[[vk::binding(0, 0)]] ConstantBuffer<BloomDownsampleUniforms> bloomDownsample;
[[vk::binding(0, 1)]] Texture2D<float4> sourceTexture;
[[vk::binding(1, 1)]] SamplerState sourceSampler;
struct ScreenInput { [[vk::location(0)]] float3 position : POSITION; };
struct ScreenOutput { float4 position : SV_Position; };
[shader("vertex")]
ScreenOutput bloomDownsampleVertex(ScreenInput input)
{
    ScreenOutput output;
    output.position = float4(input.position.xy, 0.0, 1.0);
    return output;
}
float3 sampleSource(float2 uv)
{
    return sourceTexture.SampleLevel(sourceSampler, uv, 0.0).rgb;
}
float karisWeight(float3 color)
{
    return 1.0 / (1.0 + dot(color, bloomDownsample.luminance.xyz));
}
[shader("fragment")]
float4 bloomDownsampleFragment(ScreenOutput input) : SV_Target0
{
    if (bloomDownsample.controls.w > 0.5)
        return float4(1.0, 0.0, 1.0, 1.0);
    float2 uv = saturate((input.position.xy - bloomDownsample.outputArea.xy) / max(bloomDownsample.outputArea.zw, float2(1.0)));
    float2 texel = bloomDownsample.sourceTexelThreshold.xy;
    float3 a = sampleSource(uv + texel * float2(-1.0, -1.0));
    float3 b = sampleSource(uv + texel * float2( 0.0, -1.0));
    float3 c = sampleSource(uv + texel * float2( 1.0, -1.0));
    float3 d = sampleSource(uv + texel * float2(-0.5, -0.5));
    float3 e = sampleSource(uv + texel * float2( 0.5, -0.5));
    float3 f = sampleSource(uv + texel * float2(-1.0,  0.0));
    float3 g = sampleSource(uv);
    float3 h = sampleSource(uv + texel * float2( 1.0,  0.0));
    float3 i = sampleSource(uv + texel * float2(-0.5,  0.5));
    float3 j = sampleSource(uv + texel * float2( 0.5,  0.5));
    float3 k = sampleSource(uv + texel * float2(-1.0,  1.0));
    float3 l = sampleSource(uv + texel * float2( 0.0,  1.0));
    float3 m = sampleSource(uv + texel * float2( 1.0,  1.0));
    float3 g0 = d + e + i + j;
    float3 g1 = a + b + f + g;
    float3 g2 = b + c + g + h;
    float3 g3 = f + g + k + l;
    float3 g4 = g + h + l + m;
    float3 result;
    if (bloomDownsample.controls.y > 0.5)
    {
        float w0 = karisWeight(g0 * 0.25);
        float w1 = karisWeight(g1 * 0.25);
        float w2 = karisWeight(g2 * 0.25);
        float w3 = karisWeight(g3 * 0.25);
        float w4 = karisWeight(g4 * 0.25);
        result = (g0 * (0.125 * w0) + (g1 * w1 + g2 * w2 + g3 * w3 + g4 * w4) * 0.03125)
               / (0.5 * w0 + 0.125 * (w1 + w2 + w3 + w4) + 1e-5);
    }
    else
        result = g0 * 0.125 + (g1 + g2 + g3 + g4) * 0.03125;
    if (bloomDownsample.controls.z > 0.5)
    {
        float brightness = dot(result, bloomDownsample.luminance.xyz);
        if (brightness <= 1e-5)
            result = float3(0.0);
        else
        {
            float threshold = bloomDownsample.sourceTexelThreshold.z;
            float knee = max(threshold * bloomDownsample.sourceTexelThreshold.w, 1e-5);
            float soft = clamp(brightness - threshold + knee, 0.0, 2.0 * knee);
            soft = soft * soft / (4.0 * knee + 1e-5);
            float contribution = max(soft, brightness - threshold);
            result *= contribution / brightness * bloomDownsample.controls.x;
        }
    }
    return float4(max(result, float3(0.0)), 1.0);
}
