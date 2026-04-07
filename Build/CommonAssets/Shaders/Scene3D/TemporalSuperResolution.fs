#version 450 core

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D PostProcessOutputTexture;
uniform sampler2D Velocity;
uniform sampler2D DepthView;
uniform sampler2D HistoryDepth;
uniform sampler2D TsrHistoryColor;

uniform bool HistoryReady;
uniform vec2 SourceTexelSize;
uniform vec2 HistoryTexelSize;
uniform vec2 CurrentJitterUv;
uniform vec2 PreviousJitterUv;
uniform float FeedbackMin;
uniform float FeedbackMax;
uniform float VarianceGamma;
uniform float CatmullRadius; // kept for uniform compatibility; unused by improved filter
uniform float DepthRejectThreshold;
uniform vec2 ReactiveTransparencyRange;
uniform float ReactiveVelocityScale;
uniform float ReactiveLumaThreshold;
uniform float DepthDiscontinuityScale;
uniform float ConfidencePower;
uniform int DebugMode;

const vec3 LuminanceWeights = vec3(0.2126f, 0.7152f, 0.0722f);

// ── YCoCg color space ─────────────────────────────────────────────
vec3 RGBToYCoCg(vec3 rgb)
{
    float Y  = dot(rgb, vec3(0.25f, 0.5f, 0.25f));
    float Co = dot(rgb, vec3(0.5f, 0.0f, -0.5f));
    float Cg = dot(rgb, vec3(-0.25f, 0.5f, -0.25f));
    return vec3(Y, Co, Cg);
}

vec3 YCoCgToRGB(vec3 ycocg)
{
    float Y  = ycocg.x;
    float Co = ycocg.y;
    float Cg = ycocg.z;
    return vec3(Y + Co - Cg, Y + Cg, Y - Co - Cg);
}

bool IsValidUV(vec2 uv)
{
    return all(greaterThanEqual(uv, vec2(0.0f))) && all(lessThanEqual(uv, vec2(1.0f)));
}

// ── 5-tap bicubic Catmull-Rom (Jimenez/Karis) ────────────────────
vec3 SampleCatmullRom(sampler2D tex, vec2 uv, vec2 texelSize)
{
    vec2 texSize = 1.0f / texelSize;
    vec2 position = uv * texSize;
    vec2 center = floor(position - 0.5f) + 0.5f;
    vec2 f = position - center;
    vec2 f2 = f * f;
    vec2 f3 = f2 * f;

    vec2 w0 = -0.5f * f3 + f2 - 0.5f * f;
    vec2 w1 =  1.5f * f3 - 2.5f * f2 + 1.0f;
    vec2 w2 = -1.5f * f3 + 2.0f * f2 + 0.5f * f;
    vec2 w3 =  0.5f * f3 - 0.5f * f2;

    vec2 w12 = w1 + w2;
    vec2 tc12 = (center + w2 / w12) * texelSize;
    vec2 tc0  = (center - 1.0f) * texelSize;
    vec2 tc3  = (center + 2.0f) * texelSize;

    vec3 result =
        texture(tex, vec2(tc12.x, tc12.y)).rgb * (w12.x * w12.y) +
        texture(tex, vec2(tc0.x,  tc12.y)).rgb * (w0.x  * w12.y) +
        texture(tex, vec2(tc3.x,  tc12.y)).rgb * (w3.x  * w12.y) +
        texture(tex, vec2(tc12.x, tc0.y )).rgb * (w12.x * w0.y ) +
        texture(tex, vec2(tc12.x, tc3.y )).rgb * (w12.x * w3.y );

    float totalWeight = (w12.x * w12.y) + (w0.x * w12.y) + (w3.x * w12.y)
                       + (w12.x * w0.y) + (w12.x * w3.y);
    return result / max(totalWeight, 1e-6f);
}

// Mild current-frame reconstruction filter. TSR still needs a stabilized
// current sample before history blending, otherwise the low-res jitter becomes
// obvious immediately.
vec3 SampleCurrentReconstruction(sampler2D tex, vec2 uv, vec2 texelSize)
{
    vec3 center = texture(tex, uv).rgb * 4.0f;
    vec3 axial =
        texture(tex, uv + vec2(-1.0f, 0.0f) * texelSize).rgb +
        texture(tex, uv + vec2( 1.0f, 0.0f) * texelSize).rgb +
        texture(tex, uv + vec2(0.0f, -1.0f) * texelSize).rgb +
        texture(tex, uv + vec2(0.0f,  1.0f) * texelSize).rgb;
    vec3 diagonal =
        texture(tex, uv + vec2(-1.0f, -1.0f) * texelSize).rgb +
        texture(tex, uv + vec2( 1.0f, -1.0f) * texelSize).rgb +
        texture(tex, uv + vec2(-1.0f,  1.0f) * texelSize).rgb +
        texture(tex, uv + vec2( 1.0f,  1.0f) * texelSize).rgb;
    return (center + axial * 2.0f + diagonal) * (1.0f / 16.0f);
}

// ── Clip toward AABB center (Karis 2014) ──────────────────────────
vec3 ClipToAABB(vec3 color, vec3 aabbMin, vec3 aabbMax)
{
    vec3 center  = 0.5f * (aabbMin + aabbMax);
    vec3 extents = 0.5f * (aabbMax - aabbMin) + 1e-5f;
    vec3 offset  = color - center;
    vec3 ts = abs(extents / max(abs(offset), vec3(1e-5f)));
    float t = clamp(min(ts.x, min(ts.y, ts.z)), 0.0f, 1.0f);
    return center + offset * t;
}

// ── Closest-depth velocity ────────────────────────────────────────
vec2 FindClosestVelocity(vec2 uv)
{
    vec2 closestOffset = vec2(0.0f);
    float closestDepth = 1e20f;

    for (int x = -1; x <= 1; ++x)
    {
        for (int y = -1; y <= 1; ++y)
        {
            vec2 offset = vec2(float(x), float(y)) * SourceTexelSize;
            float depth = texture(DepthView, uv + offset).r;
            if (depth < closestDepth)
            {
                closestDepth = depth;
                closestOffset = offset;
            }
        }
    }

    return texture(Velocity, uv + closestOffset).xy;
}

// ── Neighborhood bounds in YCoCg (source resolution) ──────────────
void ComputeNeighborhoodBounds(vec2 uv, out vec3 minColor, out vec3 maxColor, out vec3 meanColor)
{
    vec3 m1 = vec3(0.0f);
    vec3 m2 = vec3(0.0f);

    for (int x = -1; x <= 1; ++x)
    {
        for (int y = -1; y <= 1; ++y)
        {
            vec3 s = RGBToYCoCg(texture(PostProcessOutputTexture, uv + vec2(float(x), float(y)) * SourceTexelSize).rgb);
            m1 += s;
            m2 += s * s;
        }
    }

    meanColor = m1 / 9.0f;
    vec3 variance = max(m2 / 9.0f - meanColor * meanColor, vec3(0.0f));
    vec3 stddev = sqrt(variance);
    minColor = meanColor - VarianceGamma * stddev;
    maxColor = meanColor + VarianceGamma * stddev;
}

float EvaluateDepthDiscontinuity(vec2 uv)
{
    float centerDepth = texture(DepthView, uv).r;
    float minDepth = centerDepth;
    float maxDepth = centerDepth;
    const vec2 offsets[4] = vec2[](vec2(1.0f, 0.0f), vec2(-1.0f, 0.0f), vec2(0.0f, 1.0f), vec2(0.0f, -1.0f));
    for (int i = 0; i < 4; ++i)
    {
        float d = texture(DepthView, uv + offsets[i] * SourceTexelSize).r;
        minDepth = min(minDepth, d);
        maxDepth = max(maxDepth, d);
    }
    return clamp((maxDepth - minDepth) * DepthDiscontinuityScale, 0.0f, 1.0f);
}

float ComputeMotionMask(vec2 velocity)
{
    float motionMagnitude = length(velocity);
    float motionStart = max(ReactiveVelocityScale * 0.05f, 0.0005f);
    return smoothstep(motionStart, ReactiveVelocityScale, motionMagnitude);
}

vec3 EncodeVelocityDebug(vec2 velocity)
{
    float magnitude = clamp(length(velocity) / max(ReactiveVelocityScale, 1e-5f), 0.0f, 1.0f);
    return vec3(velocity.x * 0.25f + 0.5f, velocity.y * 0.25f + 0.5f, magnitude);
}

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x > 1.0f || clipXY.y > 1.0f)
        discard;

    vec2 uv = clipXY * 0.5f + 0.5f;

    // Closest-depth velocity for sharper silhouettes
    vec2 velocity = FindClosestVelocity(uv);
    vec2 historyUV = uv - velocity * 0.5f + (PreviousJitterUv - CurrentJitterUv);

    vec3 currentColorRaw = texture(PostProcessOutputTexture, uv).rgb;
    vec3 currentColorFiltered = SampleCurrentReconstruction(PostProcessOutputTexture, uv, SourceTexelSize);
    vec3 currentColor = mix(currentColorRaw, currentColorFiltered, 0.25f);
    vec3 currentYCoCg = RGBToYCoCg(currentColor);
    float currentLuma = currentYCoCg.x;

    // Neighborhood in YCoCg at source resolution
    vec3 minBound, maxBound, meanYCoCg;
    ComputeNeighborhoodBounds(uv, minBound, maxBound, meanYCoCg);

    float currentDepth = texture(DepthView, uv).r;
    vec3 historyYCoCg = currentYCoCg;
    bool canUseHistory = HistoryReady && IsValidUV(historyUV);

    if (canUseHistory)
    {
        float historyDepth = texture(HistoryDepth, historyUV).r;
        if (abs(historyDepth - currentDepth) <= DepthRejectThreshold)
        {
            // Bicubic Catmull-Rom on full-resolution history
            vec3 historyRGB = SampleCatmullRom(TsrHistoryColor, historyUV, HistoryTexelSize);
            historyYCoCg = RGBToYCoCg(historyRGB);
        }
        else
        {
            canUseHistory = false;
        }
    }

    // Clip toward AABB center
    vec3 clippedHistory = ClipToAABB(historyYCoCg, minBound, maxBound);
    float historyLuma = clippedHistory.x;

    float motionMask = ComputeMotionMask(velocity);
    float geometryInstability = clamp(EvaluateDepthDiscontinuity(uv) * mix(0.2f, 1.0f, motionMask), 0.0f, 1.0f);

    // Reactive mask
    vec4 currentSample = texture(PostProcessOutputTexture, uv);
    float transparencyMask = smoothstep(ReactiveTransparencyRange.x, ReactiveTransparencyRange.y, 1.0f - clamp(currentSample.a, 0.0f, 1.0f)) * max(0.15f, motionMask);
    float luminanceDelta = abs(currentLuma - historyLuma);
    float luminanceMask = smoothstep(0.25f * ReactiveLumaThreshold, ReactiveLumaThreshold, luminanceDelta) * motionMask;
    float reactiveMask = clamp(max(transparencyMask, max(motionMask, luminanceMask)), 0.0f, 1.0f);

    float confidence = pow(clamp((1.0f - geometryInstability) * (1.0f - reactiveMask), 0.0f, 1.0f), ConfidencePower);
    float staticConfidenceFloor = (1.0f - motionMask) * (1.0f - reactiveMask) * 0.5f;
    confidence = max(confidence, staticConfidenceFloor);

    float historyWeight = mix(FeedbackMin, FeedbackMax, confidence);
    if (!canUseHistory)
        historyWeight = 0.0f;

    if (DebugMode != 0)
    {
        vec3 debugColor = vec3(0.0f);
        switch (DebugMode)
        {
            case 1:
                debugColor = vec3(historyWeight);
                break;
            case 2:
                debugColor = EncodeVelocityDebug(velocity);
                break;
            case 3:
                debugColor = vec3(geometryInstability);
                break;
            case 4:
                debugColor = vec3(reactiveMask, motionMask, confidence);
                break;
            case 5:
                debugColor = canUseHistory ? vec3(0.0f, 1.0f, historyWeight) : vec3(1.0f, 0.0f, 0.0f);
                break;
        }

        OutColor = vec4(debugColor, 1.0f);
        return;
    }

    // Blend in YCoCg, convert back
    vec3 resolved = mix(currentYCoCg, clippedHistory, historyWeight);
    vec3 result = YCoCgToRGB(resolved);

    // Post-resolve sharpening — stronger than TAA because we're upscaling from low res.
    // Uses current frame detail to restore high-frequency edges.
    vec3 neighbors =
        texture(PostProcessOutputTexture, uv + vec2(-1.0f, 0.0f) * SourceTexelSize).rgb +
        texture(PostProcessOutputTexture, uv + vec2( 1.0f, 0.0f) * SourceTexelSize).rgb +
        texture(PostProcessOutputTexture, uv + vec2(0.0f, -1.0f) * SourceTexelSize).rgb +
        texture(PostProcessOutputTexture, uv + vec2(0.0f,  1.0f) * SourceTexelSize).rgb;
    vec3 highFreq = currentColorRaw - neighbors * 0.25f;
    float sharpenStrength = 0.18f * (1.0f - historyWeight) * (1.0f - 0.5f * reactiveMask);
    result += highFreq * sharpenStrength;

    OutColor = vec4(max(result, vec3(0.0f)), 1.0f);
}
