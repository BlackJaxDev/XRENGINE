// Depth/normal bilateral denoise. The vertical pass samples reduced-resolution
// input at full-resolution output UVs and uses a full-resolution TexelSize.
struct GTAOBlurUniforms
{
    float2 outputSize;
    float2 depthSize;
    float2 texelSize;
    float2 blurDirection;
    int denoiseRadius;
    float denoiseSharpness;
    int denoiseEnabled;
    int useInputNormals;
    int useNormalWeightedBlur;
    int depthMode;
    float2 outputOrigin;
};

[[vk::binding(0, 0)]] ConstantBuffer<GTAOBlurUniforms> gtaoBlur;
[[vk::binding(0, 1)]] Texture2D<float4> gtaoInputTexture;
[[vk::binding(1, 1)]] SamplerState gtaoInputSampler;
[[vk::binding(0, 2)]] DepthTexture2D depthView;
[[vk::binding(0, 3)]] Texture2D<float4> normalTexture;
[[vk::binding(1, 3)]] SamplerState normalSampler;

struct GTAOBlurVertexInput { [[vk::location(0)]] float3 position : POSITION; };

[shader("vertex")]
float4 gtaoBlurVertex(GTAOBlurVertexInput input) : SV_Position
{
    return float4(input.position.xy, 0.0, 1.0);
}

float readDepth(float2 uv)
{
    int2 texel = int2(clamp(uv * gtaoBlur.depthSize, float2(0.0), gtaoBlur.depthSize - 1.0));
    return depthView.Load(int3(texel, 0));
}

bool isFarDepth(float depth)
{
    return gtaoBlur.depthMode == 1 ? depth <= 0.000001 : depth >= 0.999999;
}

float3 readNormal(float2 uv)
{
    float2 f = normalTexture.SampleLevel(normalSampler, uv, 0.0).xy * 2.0 - 1.0;
    float3 n = float3(f, 1.0 - abs(f.x) - abs(f.y));
    float t = saturate(-n.z);
    n.xy -= float2(n.x >= 0.0 ? t : -t, n.y >= 0.0 ? t : -t);
    return normalize(n);
}

float edgeFade(float2 uv)
{
    float2 edges = min(uv, 1.0 - uv) * gtaoBlur.depthSize;
    float nearestEdge = min(edges.x, edges.y);
    float fadePixels = clamp(float(clamp(gtaoBlur.denoiseRadius, 1, 16)) * 0.5, 1.0, 4.0);
    float t = saturate(nearestEdge / fadePixels);
    return t * t * (3.0 - 2.0 * t);
}

[shader("fragment")]
float4 gtaoBlurFragment(float4 position : SV_Position) : SV_Target0
{
    float2 uv = saturate((position.xy - gtaoBlur.outputOrigin) /
        max(gtaoBlur.outputSize, float2(1.0)));
    float centerAO = gtaoInputTexture.SampleLevel(gtaoInputSampler, uv, 0.0).x;
    if (gtaoBlur.denoiseEnabled == 0 || gtaoBlur.denoiseRadius <= 0)
    {
        float value = lerp(1.0, centerAO, edgeFade(uv));
        return float4(value, value, value, 1.0);
    }

    float centerDepth = readDepth(uv);
    if (isFarDepth(centerDepth))
        return float4(1.0, 1.0, 1.0, 1.0);
    bool useNormals = gtaoBlur.useInputNormals != 0 && gtaoBlur.useNormalWeightedBlur != 0;
    float3 centerNormal = float3(0.0);
    if (useNormals)
        centerNormal = readNormal(uv);
    int radius = clamp(gtaoBlur.denoiseRadius, 1, 16);
    float sigma = max(float(radius) * 0.5, 1.0);
    float sharpness = max(gtaoBlur.denoiseSharpness, 0.001);
    float result = 0.0;
    float weightSum = 0.0;

    for (int offset = -radius; offset <= radius; ++offset)
    {
        float2 sampleUV = uv + gtaoBlur.blurDirection * gtaoBlur.texelSize * float(offset);
        if (any(sampleUV < float2(0.0)) || any(sampleUV > float2(1.0)))
            continue;
        float depth = readDepth(sampleUV);
        if (isFarDepth(depth))
            continue;
        float spatialWeight = exp(-0.5 * float(offset * offset) / (sigma * sigma));
        float depthWeight = exp(-abs(depth - centerDepth) * 24.0 * sharpness);
        float normalWeight = 1.0;
        if (useNormals)
            normalWeight = pow(max(dot(readNormal(sampleUV), centerNormal), 0.0), 1.0 + sharpness * 2.0);
        float weight = spatialWeight * depthWeight * normalWeight;
        result += gtaoInputTexture.SampleLevel(gtaoInputSampler, sampleUV, 0.0).x * weight;
        weightSum += weight;
    }
    float blurred = weightSum > 0.0 ? saturate(result / weightSum) : centerAO;
    float value = lerp(1.0, blurred, edgeFade(uv));
    return float4(value, value, value, 1.0);
}
