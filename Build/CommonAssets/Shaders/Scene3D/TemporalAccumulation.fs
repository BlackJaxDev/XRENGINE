#version 450 core

layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;
layout(location = 1) out vec2 OutExposureVariance;

uniform sampler2D TemporalColorInput;
uniform sampler2D HistoryColor;
uniform sampler2D Velocity;
uniform sampler2D DepthView;
uniform sampler2D HistoryDepth;
uniform sampler2D HistoryExposureVariance;

uniform bool HistoryReady;
uniform vec2 TexelSize;
uniform vec2 CurrentJitterUv;
uniform vec2 PreviousJitterUv;
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

bool IsValidUV(vec2 uv)
{
    return all(greaterThanEqual(uv, vec2(0.0f))) && all(lessThanEqual(uv, vec2(1.0f)));
}

vec4 CatmullRomWeights(float t)
{
    vec4 w;
    w.x = -0.5f * t + t * t - 0.5f * t * t * t;
    w.y = 1.0f - 2.5f * t * t + 1.5f * t * t * t;
    w.z = 0.5f * t + 2.0f * t * t - 1.5f * t * t * t;
    w.w = -0.5f * t * t + 0.5f * t * t * t;
    return w;
}

vec3 SampleCatmullRomFilteredColor(sampler2D tex, vec2 uv, vec2 texelSize)
{
    vec4 weights = CatmullRomWeights(0.5f);
    vec3 accumX = vec3(0.0f);
    vec3 accumY = vec3(0.0f);
    for (int i = 0; i < 4; ++i)
    {
        float offset = (float(i) - 1.5f) * CatmullRadius;
        accumX += texture(tex, uv + vec2(offset, 0.0f) * texelSize).rgb * weights[i];
        accumY += texture(tex, uv + vec2(0.0f, offset) * texelSize).rgb * weights[i];
    }
    return 0.5f * (accumX + accumY);
}

void ComputeNeighborhoodBounds(vec2 uv, out vec3 minColor, out vec3 maxColor)
{
    vec3 mean = vec3(0.0f);
    vec3 moment2 = vec3(0.0f);
    vec3 localMin = vec3(1e20f);
    vec3 localMax = vec3(-1e20f);

    for (int x = -1; x <= 1; ++x)
    {
        for (int y = -1; y <= 1; ++y)
        {
            vec3 sampleColor = texture(TemporalColorInput, uv + vec2(float(x), float(y)) * TexelSize).rgb;
            mean += sampleColor;
            moment2 += sampleColor * sampleColor;
            localMin = min(localMin, sampleColor);
            localMax = max(localMax, sampleColor);
        }
    }

    mean *= (1.0f / 9.0f);
    moment2 *= (1.0f / 9.0f);
    vec3 variance = max(moment2 - mean * mean, vec3(0.0f));
    vec3 stddev = sqrt(variance);

    vec3 varianceMin = mean - VarianceGamma * stddev;
    vec3 varianceMax = mean + VarianceGamma * stddev;
    minColor = max(localMin, varianceMin);
    maxColor = min(localMax, varianceMax);
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
    return clamp((maxDepth - minDepth) * DepthDiscontinuityScale, 0.0f, 1.0f);
}

float ComputeMotionMask(vec2 velocity)
{
    float motionMagnitude = length(velocity);
    float motionStart = max(ReactiveVelocityScale * 0.05f, 0.0005f);
    return smoothstep(motionStart, ReactiveVelocityScale, motionMagnitude);
}

float EvaluateGeometryInstability(vec2 uv, vec2 velocity)
{
    float motionMask = ComputeMotionMask(velocity);
    float depthDiscontinuity = EvaluateDepthDiscontinuity(uv);
    return clamp(depthDiscontinuity * mix(0.2f, 1.0f, motionMask), 0.0f, 1.0f);
}

float EvaluateReactiveMask(vec2 uv, vec2 velocity, float currentLuma, float historyLuma)
{
    vec4 currentSample = texture(TemporalColorInput, uv);
    float motionMask = ComputeMotionMask(velocity);
    float transparencyMask = smoothstep(ReactiveTransparencyRange.x, ReactiveTransparencyRange.y, 1.0f - clamp(currentSample.a, 0.0f, 1.0f)) * max(0.15f, motionMask);
    float velocityMask = motionMask;
    float luminanceDelta = abs(currentLuma - historyLuma);
    float luminanceMask = smoothstep(0.25f * ReactiveLumaThreshold, ReactiveLumaThreshold, luminanceDelta) * motionMask;
    return clamp(max(transparencyMask, max(velocityMask, luminanceMask)), 0.0f, 1.0f);
}

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x > 1.0f || clipXY.y > 1.0f)
        discard;

    vec2 uv = clipXY * 0.5f + 0.5f;
    vec2 velocity = texture(Velocity, uv).xy;
    vec2 historyUV = uv - velocity * 0.5f + (PreviousJitterUv - CurrentJitterUv);

    vec3 currentFiltered = SampleCatmullRomFilteredColor(TemporalColorInput, uv, TexelSize);
    float currentLuma = dot(currentFiltered, LuminanceWeights);

    vec3 minColor;
    vec3 maxColor;
    ComputeNeighborhoodBounds(uv, minColor, maxColor);

    float currentDepth = texture(DepthView, uv).r;
    vec3 historyColor = currentFiltered;
    vec2 historyStats = vec2(currentLuma, 0.0f);
    bool canUseHistory = HistoryReady && IsValidUV(historyUV);

    if (canUseHistory)
    {
        float historyDepth = texture(HistoryDepth, historyUV).r;
        if (abs(historyDepth - currentDepth) <= DepthRejectThreshold)
        {
            historyColor = SampleCatmullRomFilteredColor(HistoryColor, historyUV, TexelSize);
            historyStats = texture(HistoryExposureVariance, historyUV).rg;
        }
        else
        {
            canUseHistory = false;
        }
    }

    vec3 clippedHistory = clamp(historyColor, minColor, maxColor);
    float historyLuma = dot(clippedHistory, LuminanceWeights);
    float previousExposure = historyStats.x;
    float previousVariance = historyStats.y;

    float exposureDelta = currentLuma - previousExposure;
    float updatedExposure = mix(previousExposure, currentLuma, 0.1f);
    float updatedVariance = mix(previousVariance, exposureDelta * exposureDelta, 0.1f);

    float motionMask = ComputeMotionMask(velocity);
    float geometryInstability = EvaluateGeometryInstability(uv, velocity);
    float reactiveMask = EvaluateReactiveMask(uv, velocity, currentLuma, historyLuma);
    float confidence = pow(clamp((1.0f - geometryInstability) * (1.0f - reactiveMask), 0.0f, 1.0f), ConfidencePower);
    float staticConfidenceFloor = (1.0f - motionMask) * (1.0f - reactiveMask) * 0.5f;
    confidence = max(confidence, staticConfidenceFloor);

    float stability = exp(-updatedVariance * VarianceGamma);
    stability = max(stability, 0.85f * (1.0f - motionMask));
    float historyWeight = mix(FeedbackMin, FeedbackMax, clamp(stability * confidence, 0.0f, 1.0f));
    if (!canUseHistory)
        historyWeight = 0.0f;

    vec3 accumulated = mix(currentFiltered, clippedHistory, historyWeight);

    OutColor = vec4(accumulated, 1.0f);
    OutExposureVariance = vec2(updatedExposure, updatedVariance);
}
