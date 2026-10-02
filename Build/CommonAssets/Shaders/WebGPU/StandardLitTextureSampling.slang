// Explicit opaque deferred texture semantics. Color decoding is owned by the authored
// GPU texture format, exactly as in the desktop shader; metadata does not add a decode.
#ifndef XRE_TEXTURE_NORMAL_PREPASS
[[vk::binding(1, 1)]] Texture2D<float4> baseColorTexture;
[[vk::binding(2, 1)]] SamplerState baseColorSampler;
[[vk::binding(5, 1)]] Texture2D<float4> metallicTexture;
[[vk::binding(6, 1)]] SamplerState metallicSampler;
[[vk::binding(7, 1)]] Texture2D<float4> roughnessTexture;
[[vk::binding(8, 1)]] SamplerState roughnessSampler;
#endif
#ifdef XRE_STANDARD_LIT_TEXTURE_NORMAL
[[vk::binding(3, 1)]] Texture2D<float4> normalTexture;
[[vk::binding(4, 1)]] SamplerState normalSampler;

float3 mappedTextureNormal(float2 uv, float3 normal, float3 tangent, float3 bitangent)
{
    float3 n = normalize(normal);
    if (!all(isfinite(n))) return float3(0.0, 0.0, 1.0);
    float3 t = tangent - n * dot(n, tangent);
    float3 b = bitangent - n * dot(n, bitangent);
    if (!all(isfinite(t)) || !all(isfinite(b)) || dot(t, t) < 1e-10 || dot(b, b) < 1e-10) return n;
    float3 sample = normalTexture.Sample(normalSampler, uv).rgb * 2.0 - 1.0;
    sample.y = -sample.y;
    if (dot(sample, sample) <= 1e-6) return n;
    sample = normalize(sample);
    sample.z = max(sample.z, 0.001);
    sample = normalize(sample);
    return normalize(normalize(t) * sample.x + normalize(b) * sample.y + n * sample.z);
}
#endif
