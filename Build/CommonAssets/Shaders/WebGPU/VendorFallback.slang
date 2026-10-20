// Exact fallback blit companion of VPRC_VendorUpscale. Native vendor operations remain separate.
struct VendorFallbackUniforms
{
    float ApplySharpen;
    float FlipSourceYOnVulkanFallback;
    float EncodeOutputSrgb;
    float SharpenStrength;
};
[[vk::binding(0, 0)]] ConstantBuffer<VendorFallbackUniforms> fallback;
[[vk::binding(0, 1)]] Texture2D<float4> SourceTexture;
[[vk::binding(1, 1)]] SamplerState SourceTextureSampler;

struct ScreenInput { [[vk::location(0)]] float3 position : POSITION; };
struct ScreenOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 clipXY : TEXCOORD0;
};
[shader("vertex")]
ScreenOutput outputVertex(ScreenInput input)
{
    ScreenOutput output;
    output.position = float4(input.position.xy, 0.0, 1.0);
    output.clipXY = input.position.xy;
    return output;
}
float3 LinearToSrgb(float3 color)
{
    float3 low = color * 12.92;
    float3 high = 1.055 * pow(max(color, float3(0.0)), float3(1.0 / 2.4)) - 0.055;
    return float3(
        color.x <= 0.0031308 ? low.x : high.x,
        color.y <= 0.0031308 ? low.y : high.y,
        color.z <= 0.0031308 ? low.z : high.z);
}
[shader("fragment")]
float4 outputFragment(ScreenOutput input) : SV_Target0
{
    float2 clipXY = input.clipXY;
    if (clipXY.x < -1.0 || clipXY.x > 1.0 || clipXY.y < -1.0 || clipXY.y > 1.0)
        discard;
    float2 uv = clipXY * 0.5 + 0.5;
    if (fallback.FlipSourceYOnVulkanFallback > 0.5)
        uv.y = 1.0 - uv.y;
    float4 source = SourceTexture.SampleLevel(SourceTextureSampler, uv, 0.0);
    if (fallback.ApplySharpen > 0.5 && fallback.SharpenStrength > 0.0)
    {
        uint width, height;
        SourceTexture.GetDimensions(width, height);
        float2 texelSize = 1.0 / float2(width, height);
        float3 north = SourceTexture.SampleLevel(SourceTextureSampler, uv + float2(0.0, texelSize.y), 0.0).rgb;
        float3 south = SourceTexture.SampleLevel(SourceTextureSampler, uv - float2(0.0, texelSize.y), 0.0).rgb;
        float3 east = SourceTexture.SampleLevel(SourceTextureSampler, uv + float2(texelSize.x, 0.0), 0.0).rgb;
        float3 west = SourceTexture.SampleLevel(SourceTextureSampler, uv - float2(texelSize.x, 0.0), 0.0).rgb;
        float3 sharpened = source.rgb * (1.0 + 4.0 * fallback.SharpenStrength) - (north + south + east + west) * fallback.SharpenStrength;
        source.rgb = max(sharpened, float3(0.0));
    }
    if (fallback.EncodeOutputSrgb > 0.5)
        source.rgb = LinearToSrgb(source.rgb);
    return source;
}
