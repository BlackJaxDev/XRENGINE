#version 450 core

layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;
layout(location = 1) out vec2 OutExposureVariance;

// Sampler names must match the SamplerName property of the textures passed to the material.
// These are bound by name, not by explicit binding index.
uniform sampler2D TemporalColorInput;      // Current frame color (was CurrentColorTexture)
uniform sampler2D HistoryColor;            // History frame color (was HistoryColorTexture)
uniform sampler2D Velocity;                // Motion vectors (was VelocityTexture)
uniform sampler2D DepthView;               // Current depth (was DepthTexture)
uniform sampler2D HistoryDepth;            // History depth (was HistoryDepthTexture)
uniform sampler2D HistoryExposureVariance; // History exposure/variance (was HistoryExposureVarianceTexture)

uniform bool HistoryReady;
uniform vec2 TexelSize;
uniform float FeedbackMin;
uniform float FeedbackMax;
uniform float VarianceGamma;
uniform float CatmullRadius;
uniform float DepthRejectThreshold;
uniform vec2 ReactiveTransparencyRange;
uniform float ReactiveVelocityScale;
uniform float ReactiveLumaThreshold;
uniform float DepthDiscontinuityScale;
uniform float ConfidencePower;

const vec3 LuminanceWeights = vec3(0.2126f, 0.7152f, 0.0722f);

vec4 CatmullRomWeights(float t)
{
    vec4 w;
    w.x = -0.5f * t + t * t - 0.5f * t * t * t;
    w.y = 1.0f - 2.5f * t * t + 1.5f * t * t * t;
    w.z = 0.5f * t + 2.0f * t * t - 1.5f * t * t * t;
    w.w = -0.5f * t * t + 0.5f * t * t * t;
    return w;
}

vec3 SampleCatmullRomFilteredColor(vec2 uv)
{
    vec4 weights = CatmullRomWeights(0.5f);
    vec3 accumX = vec3(0.0f);
    vec3 accumY = vec3(0.0f);
    for (int i = 0; i < 4; ++i)
    {
        float offset = (float(i) - 1.5f) * CatmullRadius;
        accumX += texture(TemporalColorInput, uv + vec2(offset, 0.0f) * TexelSize).rgb * weights[i];
        accumY += texture(TemporalColorInput, uv + vec2(0.0f, offset) * TexelSize).rgb * weights[i];
    }
    return 0.5f * (accumX + accumY);
}

bool IsValidUV(vec2 uv)
{
    return all(greaterThanEqual(uv, vec2(0.0f))) && all(lessThanEqual(uv, vec2(1.0f)));
}

vec3 NeighborhoodClamp(vec2 uv, out vec3 maxColor)
{
    vec3 minColor = vec3(1e20f);
    maxColor = vec3(-1e20f);
    for (int x = -1; x <= 1; ++x)
    {
        for (int y = -1; y <= 1; ++y)
        {
            vec2 offset = vec2(float(x), float(y)) * TexelSize;
            vec3 sampleColor = texture(TemporalColorInput, uv + offset).rgb;
            minColor = min(minColor, sampleColor);
            maxColor = max(maxColor, sampleColor);
        }
    }
    return minColor;
}

float EvaluateDepthDiscontinuity(vec2 uv)
{
    float centerDepth = texture(DepthView, uv).r;
    float minDepth = centerDepth;
    float maxDepth = centerDepth;
    const vec2 offsets[4] = vec2[](vec2(1.0f, 0.0f), vec2(-1.0f, 0.0f), vec2(0.0f, 1.0f), vec2(0.0f, -1.0f));
    for (int i = 0; i < 4; ++i)
    {
        float sampleDepth = texture(DepthView, uv + offsets[i] * TexelSize).r;
        minDepth = min(minDepth, sampleDepth);
        maxDepth = max(maxDepth, sampleDepth);
    }
    float range = maxDepth - minDepth;
    return clamp(range * DepthDiscontinuityScale, 0.0f, 1.0f);
}

float EvaluateReactiveMask(vec2 uv, vec2 velocity, float currentLuma, float historyLuma)
{
    vec4 currentSample = texture(TemporalColorInput, uv);
    float transparencyMask = smoothstep(ReactiveTransparencyRange.x, ReactiveTransparencyRange.y, 1.0f - currentSample.a);
    float velocityMask = smoothstep(0.0f, ReactiveVelocityScale, length(velocity));
    float luminanceDelta = abs(currentLuma - historyLuma);
    float luminanceMask = smoothstep(0.25f * ReactiveLumaThreshold, ReactiveLumaThreshold, luminanceDelta);
    return clamp(max(transparencyMask, max(velocityMask, luminanceMask)), 0.0f, 1.0f);
}

void main()
{
    vec2 uv = FragPos.xy;
    if (uv.x > 1.0f || uv.y > 1.0f)
    {
        discard;
    }

    uv = uv * 0.5f + 0.5f;

    vec2 velocity = texture(Velocity, uv).xy;
    // velocity is in NDC space (-1..1). Convert to UV space by halving.
    vec2 historyUV = uv - velocity * 0.5f;

    vec3 currentFiltered = SampleCatmullRomFilteredColor(uv);
    float currentLuma = dot(currentFiltered, LuminanceWeights);
    vec3 neighborhoodMax;
    vec3 neighborhoodMin = NeighborhoodClamp(uv, neighborhoodMax);

    vec3 historyColor = currentFiltered;
    vec2 historyStats = vec2(currentLuma, 0.0f);
    bool canUseHistory = HistoryReady && IsValidUV(historyUV);
    float currentDepth = texture(DepthView, uv).r;
    float historyDepth = currentDepth;
    if (canUseHistory)
    {
        historyColor = texture(HistoryColor, historyUV).rgb;
        historyStats = texture(HistoryExposureVariance, historyUV).rg;
        historyDepth = texture(HistoryDepth, historyUV).r;
        if (abs(historyDepth - currentDepth) > DepthRejectThreshold)
        {
            canUseHistory = false;
            historyColor = currentFiltered;
            historyStats = vec2(currentLuma, 0.0f);
            historyDepth = currentDepth;
        }
    }

    vec3 clampedHistory = clamp(historyColor, neighborhoodMin - VarianceGamma, neighborhoodMax + VarianceGamma);
    float exposure = historyStats.x;
    float variance = historyStats.y;

    float exposureDelta = currentLuma - exposure;
    float updatedExposure = mix(exposure, currentLuma, 0.1f);
    float updatedVariance = mix(variance, exposureDelta * exposureDelta, 0.1f);

    float depthDiscontinuity = EvaluateDepthDiscontinuity(uv);
    float reactiveMask = EvaluateReactiveMask(uv, velocity, currentLuma, historyStats.x);
    float confidence = pow(clamp((1.0f - depthDiscontinuity) * (1.0f - reactiveMask), 0.0f, 1.0f), ConfidencePower);

    float stability = exp(-updatedVariance * VarianceGamma);
    float stabilityWithConfidence = clamp(stability * confidence, 0.0f, 1.0f);
    float historyWeight = mix(FeedbackMin, FeedbackMax, stabilityWithConfidence);
    historyWeight = canUseHistory ? historyWeight : 0.0f;

    vec3 accumulated = mix(currentFiltered, clampedHistory, historyWeight);

    OutColor = vec4(accumulated, 1.0f);
    OutExposureVariance = vec2(updatedExposure, updatedVariance);
}
