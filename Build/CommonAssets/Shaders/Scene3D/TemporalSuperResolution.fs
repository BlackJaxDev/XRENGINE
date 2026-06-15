#version 450 core

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D PostProcessOutputTexture;
uniform sampler2D Velocity;
uniform sampler2D DepthView;
uniform sampler2D HistoryDepth;
uniform sampler2D TsrHistoryColor;
// Stencil view of the post-temporal forward depth/stencil. Bit 0x80
// (XRMaterial.GizmoStencilBit) marks pixels that were drawn after TAA by
// AppendPostTemporalForwardPasses (gizmos rendered with DepthFunc=Always and
// custom near-plane vertex shaders). Those pixels have no valid temporal
// history and no valid motion vectors, so reusing history blurs/erodes them.
// We detect the bit here and force history weight to zero for such pixels.
uniform usampler2D StencilView;

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
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform vec2 ScreenOrigin;

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

vec2 ClampUvToTexels(vec2 uv, vec2 texelSize)
{
    vec2 halfTexel = texelSize * 0.5f;
    return clamp(uv, halfTexel, vec2(1.0f) - halfTexel);
}

vec2 TextureTexelSize(sampler2D tex)
{
    return 1.0f / vec2(max(textureSize(tex, 0), ivec2(1)));
}

vec2 ClampSourceUv(vec2 uv)
{
    return ClampUvToTexels(uv, TextureTexelSize(PostProcessOutputTexture));
}

vec2 ClampHistoryUv(vec2 uv)
{
    return ClampUvToTexels(uv, TextureTexelSize(TsrHistoryColor));
}

// ── 5-tap bicubic Catmull-Rom (Jimenez/Karis) ────────────────────
vec3 SampleCatmullRom(sampler2D tex, vec2 uv, vec2 texelSize)
{
    uv = ClampUvToTexels(uv, texelSize);

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
        texture(tex, ClampUvToTexels(vec2(tc12.x, tc12.y), texelSize)).rgb * (w12.x * w12.y) +
        texture(tex, ClampUvToTexels(vec2(tc0.x,  tc12.y), texelSize)).rgb * (w0.x  * w12.y) +
        texture(tex, ClampUvToTexels(vec2(tc3.x,  tc12.y), texelSize)).rgb * (w3.x  * w12.y) +
        texture(tex, ClampUvToTexels(vec2(tc12.x, tc0.y ), texelSize)).rgb * (w12.x * w0.y ) +
        texture(tex, ClampUvToTexels(vec2(tc12.x, tc3.y ), texelSize)).rgb * (w12.x * w3.y );

    float totalWeight = (w12.x * w12.y) + (w0.x * w12.y) + (w3.x * w12.y)
                       + (w12.x * w0.y) + (w12.x * w3.y);
    return result / max(totalWeight, 1e-6f);
}

// Mild current-frame reconstruction filter. TSR still needs a stabilized
// current sample before history blending, otherwise the low-res jitter becomes
// obvious immediately.
vec3 SampleCurrentReconstruction(sampler2D tex, vec2 uv, vec2 texelSize)
{
    uv = ClampUvToTexels(uv, texelSize);

    vec3 center = texture(tex, uv).rgb * 4.0f;
    vec3 axial =
        texture(tex, ClampUvToTexels(uv + vec2(-1.0f, 0.0f) * texelSize, texelSize)).rgb +
        texture(tex, ClampUvToTexels(uv + vec2( 1.0f, 0.0f) * texelSize, texelSize)).rgb +
        texture(tex, ClampUvToTexels(uv + vec2(0.0f, -1.0f) * texelSize, texelSize)).rgb +
        texture(tex, ClampUvToTexels(uv + vec2(0.0f,  1.0f) * texelSize, texelSize)).rgb;
    vec3 diagonal =
        texture(tex, ClampUvToTexels(uv + vec2(-1.0f, -1.0f) * texelSize, texelSize)).rgb +
        texture(tex, ClampUvToTexels(uv + vec2( 1.0f, -1.0f) * texelSize, texelSize)).rgb +
        texture(tex, ClampUvToTexels(uv + vec2(-1.0f,  1.0f) * texelSize, texelSize)).rgb +
        texture(tex, ClampUvToTexels(uv + vec2( 1.0f,  1.0f) * texelSize, texelSize)).rgb;
    return (center + axial * 2.0f + diagonal) * (1.0f / 16.0f);
}

float SamplePostTemporalForwardMask(vec2 uv)
{
    uint stencilBits = texture(StencilView, clamp(uv, vec2(0.0f), vec2(1.0f))).r;
    return ((stencilBits & 0x80u) != 0u) ? 1.0f : 0.0f;
}

void ComputePostTemporalForwardAA(vec2 uv, out float centerMask, out float coverage, out vec3 overlayColor)
{
    centerMask = SamplePostTemporalForwardMask(uv);
    if (centerMask <= 0.5f)
    {
        coverage = 0.0f;
        overlayColor = vec3(0.0f);
        return;
    }

    coverage = centerMask;
    uv = ClampSourceUv(uv);
    vec3 rawOverlay = texture(PostProcessOutputTexture, uv).rgb;
    vec3 filteredOverlay = SampleCurrentReconstruction(PostProcessOutputTexture, uv, TextureTexelSize(PostProcessOutputTexture));
    overlayColor = mix(rawOverlay, filteredOverlay, 0.25f);
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
    vec2 depthTexelSize = TextureTexelSize(DepthView);

    for (int x = -1; x <= 1; ++x)
    {
        for (int y = -1; y <= 1; ++y)
        {
            vec2 offset = vec2(float(x), float(y)) * depthTexelSize;
            float depth = texture(DepthView, ClampSourceUv(uv + offset)).r;
            if (depth < closestDepth)
            {
                closestDepth = depth;
                closestOffset = offset;
            }
        }
    }

    return texture(Velocity, ClampSourceUv(uv + closestOffset)).xy;
}

// ── Neighborhood bounds in YCoCg (source resolution) ──────────────
void ComputeNeighborhoodBounds(vec2 uv, out vec3 minColor, out vec3 maxColor, out vec3 meanColor)
{
    vec3 m1 = vec3(0.0f);
    vec3 m2 = vec3(0.0f);
    vec2 sourceTexelSize = TextureTexelSize(PostProcessOutputTexture);

    for (int x = -1; x <= 1; ++x)
    {
        for (int y = -1; y <= 1; ++y)
        {
            vec2 sampleUv = ClampSourceUv(uv + vec2(float(x), float(y)) * sourceTexelSize);
            vec3 s = RGBToYCoCg(texture(PostProcessOutputTexture, sampleUv).rgb);
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
    uv = ClampSourceUv(uv);
    vec2 depthTexelSize = TextureTexelSize(DepthView);

    float centerDepth = texture(DepthView, uv).r;
    float minDepth = centerDepth;
    float maxDepth = centerDepth;
    const vec2 offsets[4] = vec2[](vec2(1.0f, 0.0f), vec2(-1.0f, 0.0f), vec2(0.0f, 1.0f), vec2(0.0f, -1.0f));
    for (int i = 0; i < 4; ++i)
    {
        float d = texture(DepthView, ClampSourceUv(uv + offsets[i] * depthTexelSize)).r;
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
    if (clipXY.x < -1.0f || clipXY.x > 1.0f || clipXY.y < -1.0f || clipXY.y > 1.0f)
        discard;

    vec2 uv = ClampSourceUv(
        XRENGINE_FramebufferUV(gl_FragCoord.xy, ScreenOrigin, vec2(ScreenWidth, ScreenHeight)));

    // MotionVectors.fs writes unjittered current-minus-previous NDC, so do
    // not apply the temporal jitter delta again here.
    float depthDiscontinuity = EvaluateDepthDiscontinuity(uv);
    vec2 velocity = texture(Velocity, uv).xy;
    if (depthDiscontinuity > 1e-4f)
        velocity = FindClosestVelocity(uv);

    float postTemporalCenterMask;
    float postTemporalCoverage;
    vec3 postTemporalOverlayColor;
    ComputePostTemporalForwardAA(uv, postTemporalCenterMask, postTemporalCoverage, postTemporalOverlayColor);

    bool isPostTemporalForward = postTemporalCenterMask > 0.5f;
    if (isPostTemporalForward)
        velocity = vec2(0.0f);

    vec2 historyUV = uv - velocity * 0.5f;
    vec2 sourceTexelSize = TextureTexelSize(PostProcessOutputTexture);
    vec2 historyTexelSize = TextureTexelSize(TsrHistoryColor);

    vec3 currentColorRaw = texture(PostProcessOutputTexture, uv).rgb;
    vec3 currentColorFiltered = SampleCurrentReconstruction(PostProcessOutputTexture, uv, sourceTexelSize);
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
        vec2 historySampleUv = ClampHistoryUv(historyUV);
        float historyDepth = texture(HistoryDepth, historySampleUv).r;
        if (abs(historyDepth - currentDepth) <= DepthRejectThreshold)
        {
            // Bicubic Catmull-Rom on full-resolution history
            vec3 historyRGB = SampleCatmullRom(TsrHistoryColor, historySampleUv, historyTexelSize);
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
    float geometryInstability = clamp(depthDiscontinuity * mix(0.2f, 1.0f, motionMask), 0.0f, 1.0f);

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
    if (isPostTemporalForward)
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
    // Uses current frame detail to restore high-frequency edges. Skip the
    // overlay neighborhood; its coverage is resolved below from the stencil mask.
    if (postTemporalCoverage <= 0.0f)
    {
        vec3 neighbors =
            texture(PostProcessOutputTexture, ClampSourceUv(uv + vec2(-1.0f, 0.0f) * sourceTexelSize)).rgb +
            texture(PostProcessOutputTexture, ClampSourceUv(uv + vec2( 1.0f, 0.0f) * sourceTexelSize)).rgb +
            texture(PostProcessOutputTexture, ClampSourceUv(uv + vec2(0.0f, -1.0f) * sourceTexelSize)).rgb +
            texture(PostProcessOutputTexture, ClampSourceUv(uv + vec2(0.0f,  1.0f) * sourceTexelSize)).rgb;
        vec3 highFreq = currentColorRaw - neighbors * 0.25f;
        float sharpenStrength = 0.18f * (1.0f - historyWeight) * (1.0f - 0.5f * reactiveMask);
        result += highFreq * sharpenStrength;
    }

    if (postTemporalCoverage > 0.0f)
        result = mix(result, postTemporalOverlayColor, postTemporalCoverage);

    OutColor = vec4(max(result, vec3(0.0f)), 1.0f);
}
