// Composite bloom into linear HDR before exposure/Mobius/gamma. Each bloom
// level is a separate sampled texture because the browser host binds XRTexture2D.
struct BloomCombineUniforms
{
    float4 outputArea; // origin xy, extent zw in physical pixels.
    float4 controls;   // Strength, StartMip, EndMip, DebugBloomOnly.
    float4 weights0To3;
    float4 weights4;   // x = level-4 weight.
};
[[vk::binding(0, 0)]] ConstantBuffer<BloomCombineUniforms> bloomCombine;
[[vk::binding(0, 1)]] Texture2D<float4> hdrTexture;
[[vk::binding(1, 1)]] Texture2D<float4> bloomMip0;
[[vk::binding(2, 1)]] Texture2D<float4> bloomMip1;
[[vk::binding(3, 1)]] Texture2D<float4> bloomMip2;
[[vk::binding(4, 1)]] Texture2D<float4> bloomMip3;
[[vk::binding(5, 1)]] Texture2D<float4> bloomMip4;
[[vk::binding(6, 1)]] SamplerState hdrSampler;
[[vk::binding(7, 1)]] SamplerState bloomSampler0;
[[vk::binding(8, 1)]] SamplerState bloomSampler1;
[[vk::binding(9, 1)]] SamplerState bloomSampler2;
[[vk::binding(10, 1)]] SamplerState bloomSampler3;
[[vk::binding(11, 1)]] SamplerState bloomSampler4;
struct ScreenInput { [[vk::location(0)]] float3 position : POSITION; };
struct ScreenOutput { float4 position : SV_Position; };
[shader("vertex")]
ScreenOutput bloomCombineVertex(ScreenInput input)
{
    ScreenOutput output;
    output.position = float4(input.position.xy, 0.0, 1.0);
    return output;
}
float3 sampleBloom(int level, float2 uv)
{
    if (level == 0) return max(bloomMip0.SampleLevel(bloomSampler0, uv, 0.0).rgb, float3(0.0));
    if (level == 1) return max(bloomMip1.SampleLevel(bloomSampler1, uv, 0.0).rgb, float3(0.0));
    if (level == 2) return max(bloomMip2.SampleLevel(bloomSampler2, uv, 0.0).rgb, float3(0.0));
    if (level == 3) return max(bloomMip3.SampleLevel(bloomSampler3, uv, 0.0).rgb, float3(0.0));
    return max(bloomMip4.SampleLevel(bloomSampler4, uv, 0.0).rgb, float3(0.0));
}
float mipWeight(int level)
{
    if (level == 0) return bloomCombine.weights0To3.x;
    if (level == 1) return bloomCombine.weights0To3.y;
    if (level == 2) return bloomCombine.weights0To3.z;
    if (level == 3) return bloomCombine.weights0To3.w;
    return bloomCombine.weights4.x;
}
[shader("fragment")]
float4 bloomCombineFragment(ScreenOutput input) : SV_Target0
{
    float2 uv = saturate((input.position.xy - bloomCombine.outputArea.xy) / max(bloomCombine.outputArea.zw, float2(1.0)));
    if (bloomCombine.controls.w > 0.5)
    {
        // Desktop's 2x2 raw/filtered mip diagnostic, with mip-colored borders.
        int col = uv.x < 0.5 ? 0 : 1;
        int row = uv.y < 0.5 ? 0 : 1;
        int level = row * 2 + col;
        float2 cellUV = frac(uv * 2.0);
        bool border = cellUV.x < 0.005 || cellUV.x > 0.995 || cellUV.y < 0.005 || cellUV.y > 0.995;
        float3 borderColor = level == 0 ? float3(1.0, 0.0, 0.0) :
                             level == 1 ? float3(0.0, 1.0, 0.0) :
                             level == 2 ? float3(0.0, 0.0, 1.0) : float3(1.0, 1.0, 0.0);
        return float4(border ? borderColor : sampleBloom(level, cellUV), 1.0);
    }
    float4 hdr = hdrTexture.SampleLevel(hdrSampler, uv, 0.0);
    float3 color = max(hdr.rgb, float3(0.0));
    if (bloomCombine.controls.x > 0.0)
    {
        int startMip = clamp(int(bloomCombine.controls.y), 0, 4);
        int endMip = clamp(int(bloomCombine.controls.z), startMip, 4);
        if (startMip == 1 && endMip == 4)
            color += sampleBloom(1, uv) * bloomCombine.controls.x;
        else
        {
            for (int level = startMip; level <= endMip; ++level)
            {
                float weight = mipWeight(level);
                if (weight > 0.0)
                    color += sampleBloom(level, uv) * weight * bloomCombine.controls.x;
            }
        }
    }
    return float4(color, hdr.w);
}
