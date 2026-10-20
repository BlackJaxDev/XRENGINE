// GPU texture formats own color decoding, exactly as in the desktop source.
[[vk::binding(1, 1)]] Texture2D<float4> baseColorTexture;
[[vk::binding(2, 1)]] SamplerState baseColorSampler;
[[vk::binding(3, 1)]] Texture2D<float4> opacityTexture;
[[vk::binding(4, 1)]] SamplerState opacitySampler;
float4 texturedAlphaSurface(float2 uv)
{
    float4 color = baseColorTexture.Sample(baseColorSampler, uv);
    color.a *= opacityTexture.Sample(opacitySampler, uv).r;
    return color;
}
