// Exact mono browser companion of the shared Advanced output operation.
struct OutputUniforms
{
    float ScreenWidth;
    float ScreenHeight;
    float2 ScreenOrigin;
    float2 TexelSize;
    float ShutterScale;
    float VelocityThreshold;
    float DepthRejectThreshold;
    float MaxBlurPixels;
    float SampleFalloff;
    int MaxSamples;
};
[[vk::binding(0, 0)]] ConstantBuffer<OutputUniforms> outputUniforms;
[[vk::binding(0, 1)]] Texture2D<float4> MotionBlur;
[[vk::binding(1, 1)]] SamplerState MotionBlurSampler;
[[vk::binding(2, 1)]] Texture2D<float4> Velocity;
[[vk::binding(3, 1)]] SamplerState VelocitySampler;
[[vk::binding(4, 1)]] DepthTexture2D DepthView;

struct ScreenInput { [[vk::location(0)]] float3 position : POSITION; };
struct ScreenOutput { float4 position : SV_Position; };
[shader("vertex")]
ScreenOutput outputVertex(ScreenInput input)
{
    ScreenOutput output; output.position = float4(input.position.xy, 0.0, 1.0); return output;
}
#define ScreenWidth outputUniforms.ScreenWidth
#define ScreenHeight outputUniforms.ScreenHeight
#define ScreenOrigin outputUniforms.ScreenOrigin
#define TexelSize outputUniforms.TexelSize
#define ShutterScale outputUniforms.ShutterScale
#define VelocityThreshold outputUniforms.VelocityThreshold
#define DepthRejectThreshold outputUniforms.DepthRejectThreshold
#define MaxBlurPixels outputUniforms.MaxBlurPixels
#define SampleFalloff outputUniforms.SampleFalloff
#define MaxSamples outputUniforms.MaxSamples

float ReadDepth(float2 uv)
{
    uint width, height; DepthView.GetDimensions(width, height);
    int2 pixel = min(int2(saturate(uv) * float2(width, height)), int2(width, height) - 1);
    return DepthView.Load(int3(pixel, 0));
}
bool IsValidUv(float2 uv)
{
    return all(uv >= float2(0.0f)) && all(uv <= float2(1.0f));
}

bool DepthAccepted(float2 testUv, float baseDepth)
{
    if (DepthRejectThreshold <= 0.0f)
        return true;
    return abs(ReadDepth(testUv) - baseDepth) <= DepthRejectThreshold;
}

[shader("fragment")]
float4 outputFragment(ScreenOutput input) : SV_Target0
{
    float2 uv = clamp(
        (input.position.xy - ScreenOrigin) / max(float2(ScreenWidth, ScreenHeight), float2(1.0)),
        float2(0.0f),
        float2(1.0f));
    float4 baseColor = MotionBlur.SampleLevel(MotionBlurSampler, uv, 0.0);
    float2 velocity = Velocity.SampleLevel(VelocitySampler, uv, 0.0).xy;
    float depth = ReadDepth(uv);

    if (MaxSamples <= 1)
    {
        return baseColor;
    }

    float motionMagnitude = length(velocity);
    if (motionMagnitude < VelocityThreshold || ShutterScale <= 0.0f)
    {
        return baseColor;
    }

    // Velocity is encoded in NDC like the temporal pass expects. Convert to UV/pixel
    // space before building the blur kernel so both consumers interpret the buffer identically.
    // The canonical velocity is NDC Y-up; WebGPU framebuffer UV is Y-down.
    float2 velocityUv = velocity * float2(0.5f, -0.5f);
    float2 texelSize = max(TexelSize, float2(1e-5f));
    float2 velocityPixels = velocityUv / texelSize;
    float motionMagnitudePixels = length(velocityPixels);
    if (motionMagnitudePixels <= 1e-5f)
    {
        return baseColor;
    }

    float blurPixels = min(motionMagnitudePixels * ShutterScale, MaxBlurPixels);
    if (blurPixels <= 1e-5f)
    {
        return baseColor;
    }

    float2 blurVector = (velocityPixels / motionMagnitudePixels) * (blurPixels * texelSize);
    float blurRatio = clamp(blurPixels / max(MaxBlurPixels, 1e-5f), 0.0f, 1.0f);
    int steps = max(1, int(ceil(blurRatio * float(MaxSamples))));

    float3 accum = baseColor.rgb;
    float totalWeight = 1.0f;

    for (int i = 1; i <= steps; ++i)
    {
        float t = float(i) / float(steps);
        float weight = exp(-t * SampleFalloff);
        float2 offset = blurVector * t;

        float2 uvPos = uv + offset;
        if (IsValidUv(uvPos) && DepthAccepted(uvPos, depth))
        {
            accum += MotionBlur.SampleLevel(MotionBlurSampler, uvPos, 0.0).rgb * weight;
            totalWeight += weight;
        }

        float2 uvNeg = uv - offset;
        if (IsValidUv(uvNeg) && DepthAccepted(uvNeg, depth))
        {
            accum += MotionBlur.SampleLevel(MotionBlurSampler, uvNeg, 0.0).rgb * weight;
            totalWeight += weight;
        }
    }

    return float4(accum / max(totalWeight, 1e-4f), baseColor.a);
}
