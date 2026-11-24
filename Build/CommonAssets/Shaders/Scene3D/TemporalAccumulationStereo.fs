#version 460
#extension GL_OVR_multiview2 : require

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;
layout(location = 1) out vec2 OutExposureVariance;

uniform sampler2DArray TemporalColorInput;
uniform sampler2DArray HistoryColor;
uniform sampler2DArray Velocity;
uniform sampler2DArray DepthView;
uniform sampler2DArray HistoryDepth;
uniform sampler2DArray HistoryExposureVariance;

#ifdef XR_ADVANCED_REACTIVE_MASK
// Canonical opaque/late reactivity is independent of final output alpha.
uniform sampler2DArray AdvancedReactiveMask;
#endif

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
uniform int DebugMode;
uniform int AntiAliasingFilterMode;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform vec2 ScreenOrigin;

vec3 EyeUv(vec2 uv)
{
    return vec3(uv, float(gl_ViewID_OVR));
}

vec3 RGBToYCoCg(vec3 rgb)
{
    float y = dot(rgb, vec3(0.25, 0.5, 0.25));
    float co = dot(rgb, vec3(0.5, 0.0, -0.5));
    float cg = dot(rgb, vec3(-0.25, 0.5, -0.25));
    return vec3(y, co, cg);
}

vec3 YCoCgToRGB(vec3 ycocg)
{
    float y = ycocg.x;
    float co = ycocg.y;
    float cg = ycocg.z;
    return vec3(y + co - cg, y + cg, y - co - cg);
}

bool IsValidUV(vec2 uv)
{
    return all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0)));
}

vec3 ClipToAABB(vec3 color, vec3 aabbMin, vec3 aabbMax)
{
    vec3 center = 0.5 * (aabbMin + aabbMax);
    vec3 extents = 0.5 * (aabbMax - aabbMin) + 1e-5;
    vec3 offset = color - center;
    vec3 ts = abs(extents / max(abs(offset), vec3(1e-5)));
    float t = clamp(min(ts.x, min(ts.y, ts.z)), 0.0, 1.0);
    return center + offset * t;
}

vec3 SampleCurrentReconstruction(sampler2DArray tex, vec2 uv)
{
    vec3 center = texture(tex, EyeUv(uv)).rgb * 4.0;
    vec3 axial =
        texture(tex, EyeUv(uv + vec2(-1.0, 0.0) * TexelSize)).rgb +
        texture(tex, EyeUv(uv + vec2( 1.0, 0.0) * TexelSize)).rgb +
        texture(tex, EyeUv(uv + vec2(0.0, -1.0) * TexelSize)).rgb +
        texture(tex, EyeUv(uv + vec2(0.0,  1.0) * TexelSize)).rgb;
    vec3 diagonal =
        texture(tex, EyeUv(uv + vec2(-1.0, -1.0) * TexelSize)).rgb +
        texture(tex, EyeUv(uv + vec2( 1.0, -1.0) * TexelSize)).rgb +
        texture(tex, EyeUv(uv + vec2(-1.0,  1.0) * TexelSize)).rgb +
        texture(tex, EyeUv(uv + vec2( 1.0,  1.0) * TexelSize)).rgb;
    return (center + axial * 2.0 + diagonal) * (1.0 / 16.0);
}

// Integrates the 1D tent kernel W(x) = 1 - |x| on [-1, 1] over interval [aIn, bIn].
float Tent1DIntegral(float aIn, float bIn)
{
    float a = clamp(aIn, -1.0, 1.0);
    float b = clamp(bIn, -1.0, 1.0);
    if (b <= a)
        return 0.0;
    return (b - 0.5 * b * abs(b)) - (a - 0.5 * a * abs(a));
}

// Integrates the 2x2 bilinear tent kernel W(x, y) = (1 - |x|)(1 - |y|) across
// the overlapping 3x3 texel squares of the active multiview layer.
vec3 SampleTent2x2(sampler2DArray tex, vec2 uv)
{
    ivec2 extent = max(textureSize(tex, 0).xy, ivec2(1));
    int layer = int(gl_ViewID_OVR);
    vec2 position = clamp(uv, vec2(0.0), vec2(1.0)) * vec2(extent);
    ivec2 centerPixel = ivec2(floor(position));
    float wx[3];
    float wy[3];
    for (int i = -1; i <= 1; ++i)
    {
        wx[i + 1] = Tent1DIntegral(float(centerPixel.x + i) - position.x,
            float(centerPixel.x + i + 1) - position.x);
        wy[i + 1] = Tent1DIntegral(float(centerPixel.y + i) - position.y,
            float(centerPixel.y + i + 1) - position.y);
    }

    vec3 colorSum = vec3(0.0);
    float weightSum = 0.0;
    for (int y = -1; y <= 1; ++y)
    {
        for (int x = -1; x <= 1; ++x)
        {
            float weight = wx[x + 1] * wy[y + 1];
            if (weight <= 1e-6)
                continue;
            ivec2 samplePixel = clamp(centerPixel + ivec2(x, y), ivec2(0), extent - ivec2(1));
            colorSum += texelFetch(tex, ivec3(samplePixel, layer), 0).rgb * weight;
            weightSum += weight;
        }
    }
    return colorSum / max(weightSum, 1e-6);
}

vec2 FindClosestVelocity(vec2 uv)
{
    vec2 closestOffset = vec2(0.0);
    float closestDepth = 1e20;

    for (int x = -1; x <= 1; ++x)
    {
        for (int y = -1; y <= 1; ++y)
        {
            vec2 sampleUv = clamp(uv + vec2(float(x), float(y)) * TexelSize, vec2(0.0), vec2(1.0));
            float depth = texture(DepthView, EyeUv(sampleUv)).r;
            if (depth < closestDepth)
            {
                closestDepth = depth;
                closestOffset = sampleUv - uv;
            }
        }
    }

    return texture(Velocity, EyeUv(clamp(uv + closestOffset, vec2(0.0), vec2(1.0)))).xy;
}

void ComputeNeighborhoodBounds(vec2 uv, out vec3 minColor, out vec3 maxColor, out vec3 meanColor)
{
    vec3 m1 = vec3(0.0);
    vec3 m2 = vec3(0.0);

    for (int x = -1; x <= 1; ++x)
    {
        for (int y = -1; y <= 1; ++y)
        {
            vec2 sampleUv = clamp(uv + vec2(float(x), float(y)) * TexelSize, vec2(0.0), vec2(1.0));
            vec3 s = RGBToYCoCg(texture(TemporalColorInput, EyeUv(sampleUv)).rgb);
            m1 += s;
            m2 += s * s;
        }
    }

    meanColor = m1 / 9.0;
    vec3 variance = max(m2 / 9.0 - meanColor * meanColor, vec3(0.0));
    vec3 stddev = sqrt(variance);
    minColor = meanColor - VarianceGamma * stddev;
    maxColor = meanColor + VarianceGamma * stddev;
}

float EvaluateDepthDiscontinuity(vec2 uv)
{
    float centerDepth = texture(DepthView, EyeUv(uv)).r;
    float minDepth = centerDepth;
    float maxDepth = centerDepth;
    const vec2 offsets[4] = vec2[](vec2(1.0, 0.0), vec2(-1.0, 0.0), vec2(0.0, 1.0), vec2(0.0, -1.0));
    for (int i = 0; i < 4; ++i)
    {
        float d = texture(DepthView, EyeUv(clamp(uv + offsets[i] * TexelSize, vec2(0.0), vec2(1.0)))).r;
        minDepth = min(minDepth, d);
        maxDepth = max(maxDepth, d);
    }
    return clamp((maxDepth - minDepth) * DepthDiscontinuityScale, 0.0, 1.0);
}

vec3 EncodeVelocityDebug(vec2 velocity)
{
    float magnitude = clamp(length(velocity) / max(ReactiveVelocityScale, 1e-5), 0.0, 1.0);
    return vec3(velocity.x * 0.25 + 0.5, velocity.y * 0.25 + 0.5, magnitude);
}

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x > 1.0 || clipXY.y > 1.0)
        discard;

    vec2 uv = clamp(
        XRENGINE_FramebufferUV(gl_FragCoord.xy, ScreenOrigin, vec2(ScreenWidth, ScreenHeight)),
        vec2(0.0),
        vec2(1.0));

    vec2 velocity = FindClosestVelocity(uv);
    vec2 historyUV = uv - velocity * 0.5 + PreviousJitterUv - CurrentJitterUv;

    vec4 currentSample = texture(TemporalColorInput, EyeUv(uv));
    vec3 currentColorRaw = currentSample.rgb;
    vec3 currentColor;
    if (AntiAliasingFilterMode == 1)
        currentColor = currentColorRaw;
    else if (AntiAliasingFilterMode == 2)
        currentColor = SampleTent2x2(TemporalColorInput, uv);
    else
        currentColor = mix(currentColorRaw, SampleCurrentReconstruction(TemporalColorInput, uv), 0.18);
    vec3 currentYCoCg = RGBToYCoCg(currentColor);
    float currentLuma = currentYCoCg.x;

    vec3 minBound;
    vec3 maxBound;
    vec3 meanYCoCg;
    ComputeNeighborhoodBounds(uv, minBound, maxBound, meanYCoCg);

    float currentDepth = texture(DepthView, EyeUv(uv)).r;
    vec3 historyYCoCg = currentYCoCg;
    vec2 historyStats = vec2(currentLuma, 0.0);
    bool canUseHistory = HistoryReady && IsValidUV(historyUV);

    if (canUseHistory)
    {
        vec2 historySampleUv = clamp(historyUV, vec2(0.0), vec2(1.0));
        float historyDepth = texture(HistoryDepth, EyeUv(historySampleUv)).r;
        if (abs(historyDepth - currentDepth) <= DepthRejectThreshold)
        {
            vec3 historyRgb = AntiAliasingFilterMode == 2
                ? SampleTent2x2(HistoryColor, historySampleUv)
                : texture(HistoryColor, EyeUv(historySampleUv)).rgb;
            historyYCoCg = RGBToYCoCg(historyRgb);
            historyStats = texture(HistoryExposureVariance, EyeUv(historySampleUv)).rg;
        }
        else
        {
            canUseHistory = false;
        }
    }

    vec3 clippedHistory = ClipToAABB(historyYCoCg, minBound, maxBound);
    float historyLuma = clippedHistory.x;
    float previousExposure = historyStats.x;
    float previousVariance = historyStats.y;
    float exposureDelta = currentLuma - previousExposure;
    float updatedExposure = mix(previousExposure, currentLuma, 0.1);
    float updatedVariance = mix(previousVariance, exposureDelta * exposureDelta, 0.1);

    float motionMagnitude = length(velocity);
    float motionMask = smoothstep(max(ReactiveVelocityScale * 0.05, 0.0005), ReactiveVelocityScale, motionMagnitude);
    float geometryInstability = EvaluateDepthDiscontinuity(uv) * mix(0.12, 1.0, motionMask);
    float transparencyMask = smoothstep(ReactiveTransparencyRange.x, ReactiveTransparencyRange.y, 1.0 - clamp(currentSample.a, 0.0, 1.0)) * max(0.15, motionMask);
    float luminanceMask = smoothstep(0.25 * ReactiveLumaThreshold, ReactiveLumaThreshold, abs(currentLuma - historyLuma)) * motionMask;
    float reactiveMask = clamp(max(transparencyMask, max(motionMask, luminanceMask)), 0.0, 1.0);
#ifdef XR_ADVANCED_REACTIVE_MASK
    reactiveMask = max(reactiveMask, clamp(texture(AdvancedReactiveMask, EyeUv(uv)).r, 0.0, 1.0));
#endif
    float confidence = pow(clamp((1.0 - geometryInstability) * (1.0 - reactiveMask), 0.0, 1.0), ConfidencePower);
    float stability = max(exp(-updatedVariance * VarianceGamma), 0.85 * (1.0 - motionMask));
    float historyWeight = canUseHistory ? mix(FeedbackMin, FeedbackMax, clamp(stability * confidence, 0.0, 1.0)) : 0.0;

    if (DebugMode != 0)
    {
        vec3 debugColor = vec3(historyWeight);
        if (DebugMode == 2)
            debugColor = EncodeVelocityDebug(velocity);
        else if (DebugMode == 3)
            debugColor = vec3(geometryInstability);
        else if (DebugMode == 4)
            debugColor = vec3(reactiveMask, motionMask, confidence);
        else if (DebugMode == 5)
            debugColor = canUseHistory ? vec3(0.0, 1.0, historyWeight) : vec3(1.0, 0.0, 0.0);
        OutColor = vec4(debugColor, 1.0);
        OutExposureVariance = vec2(updatedExposure, updatedVariance);
        return;
    }

    vec3 result = YCoCgToRGB(mix(currentYCoCg, clippedHistory, historyWeight));
    OutColor = vec4(max(result, vec3(0.0)), 1.0);
    OutExposureVariance = vec2(updatedExposure, updatedVariance);
}
