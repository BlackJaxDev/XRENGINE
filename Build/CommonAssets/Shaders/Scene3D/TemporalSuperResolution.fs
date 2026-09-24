#version 450 core

#pragma snippet "ScreenSpaceUtils"
#pragma snippet "TemporalSuperResolutionCore"

layout(location = 0) out vec4 OutColor;
#ifdef XR_TSR_STABLE_OUTPUT
layout(location = 1) out vec4 OutHistoryMetadata;
layout(location = 2) out vec4 OutAccumulation;
uniform sampler2D TsrHistoryMetadata;
#endif
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

#ifdef XR_ADVANCED_REACTIVE_MASK
// Canonical opaque/late reactivity is independent of final output alpha.
uniform sampler2D AdvancedReactiveMask;
#endif

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

vec2 ClampUvToTexels(vec2 uv, vec2 texelSize)
{
    return TsrClampUvToTexels(uv, texelSize);
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
void ComputeNeighborhoodBounds(vec2 uv, out vec3 minColor, out vec3 maxColor,
    out vec3 meanColor, out vec3 sampleMin, out vec3 sampleMax)
{
    sampleMin = vec3(1e20);
    sampleMax = vec3(-1e20);
    vec3 m1 = vec3(0.0f);
    vec3 m2 = vec3(0.0f);
    vec2 sourceTexelSize = TextureTexelSize(PostProcessOutputTexture);

    for (int x = -1; x <= 1; ++x)
    {
        for (int y = -1; y <= 1; ++y)
        {
            vec2 sampleUv = ClampSourceUv(uv + vec2(float(x), float(y)) * sourceTexelSize);
            vec3 s = TsrRgbToYCoCg(texture(PostProcessOutputTexture, sampleUv).rgb);
            sampleMin = min(sampleMin, s);
            sampleMax = max(sampleMax, s);
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

vec3 EncodeVelocityDebug(vec2 velocity)
{
    float magnitude = clamp(length(velocity) / max(ReactiveVelocityScale, 1e-5f), 0.0f, 1.0f);
    return vec3(velocity.x * 0.25f + 0.5f, velocity.y * 0.25f + 0.5f, magnitude);
}

#ifdef XR_TSR_STABLE_OUTPUT
float TsrLoadCurrentDepth(ivec2 pixel) { return texelFetch(DepthView, pixel, 0).r; }
vec2 TsrLoadVelocity(ivec2 pixel) { return texelFetch(Velocity, pixel, 0).xy; }
float TsrLoadHistoryDepth(ivec2 pixel) { return texelFetch(HistoryDepth, pixel, 0).r; }
vec4 TsrLoadHistoryColor(ivec2 pixel) { return texelFetch(TsrHistoryColor, pixel, 0); }
vec4 TsrLoadHistoryMetadata(ivec2 pixel) { return texelFetch(TsrHistoryMetadata, pixel, 0); }
vec3 TsrLoadCurrentColor(ivec2 pixel) { return texelFetch(PostProcessOutputTexture, pixel, 0).rgb; }
float TsrLoadCurrentReactivity(ivec2 pixel) { return texelFetch(AdvancedReactiveMask, pixel, 0).r; }
#endif
#pragma snippet "TemporalSuperResolutionSurface"
#pragma snippet "TemporalSuperResolutionCoverage"

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x < -1.0f || clipXY.x > 1.0f || clipXY.y < -1.0f || clipXY.y > 1.0f)
        discard;

    vec2 outputUv = XRENGINE_FramebufferUV(
        gl_FragCoord.xy, ScreenOrigin, vec2(ScreenWidth, ScreenHeight));
    vec2 currentJitter = TsrFramebufferUvDisplacement(CurrentJitterUv, FramebufferTextureYDirection);
    vec2 previousJitter = TsrFramebufferUvDisplacement(PreviousJitterUv, FramebufferTextureYDirection);
#ifdef XR_TSR_STABLE_OUTPUT
    // Advanced draws its scene and overlays before removing projection jitter.
    vec2 sourceUv = outputUv + currentJitter;
#else
    // Default composites unjittered overlays before TSR and retains its current grid.
    vec2 sourceUv = outputUv;
#endif
    vec2 uv = ClampSourceUv(sourceUv);
    float postTemporalCoverage = SamplePostTemporalForwardMask(uv);
    bool isPostTemporalForward = postTemporalCoverage > 0.5f;

    float depthDiscontinuity = EvaluateDepthDiscontinuity(uv);
#ifdef XR_TSR_STABLE_OUTPUT
    TsrSurfaceSample surface = TsrSelectSurface(uv);
    vec2 velocity = surface.velocity;
#else
    vec2 velocity = texture(Velocity, uv).xy;
    if (depthDiscontinuity > 1e-4f)
        velocity = FindClosestVelocity(uv);
#endif

    if (isPostTemporalForward)
        velocity = vec2(0.0f);

#ifdef XR_TSR_STABLE_OUTPUT
    // Color history is stable; its raw depth companion retains previous jitter.
    vec2 historyUV = outputUv - TsrFramebufferUvDisplacement(velocity * 0.5f, FramebufferTextureYDirection);
    vec2 historyDepthUv = historyUV + previousJitter;
#else
    vec2 historyUV = uv - TsrFramebufferUvDisplacement(velocity * 0.5f, FramebufferTextureYDirection)
        + previousJitter - currentJitter;
    vec2 historyDepthUv = historyUV;
#endif
    vec2 sourceTexelSize = TextureTexelSize(PostProcessOutputTexture);
    vec2 historyTexelSize = TextureTexelSize(TsrHistoryColor);
    bool nativeResolution = all(equal(
        textureSize(PostProcessOutputTexture, 0),
        textureSize(TsrHistoryColor, 0)));

    vec3 currentColorRaw = texture(PostProcessOutputTexture, uv).rgb;
#ifdef XR_TSR_STABLE_OUTPUT
    // Surface ownership and current color use the same positive bilinear taps.
    vec3 currentColor = currentColorRaw;
#else
    vec3 currentColorFiltered = SampleCurrentReconstruction(PostProcessOutputTexture, uv, sourceTexelSize);
    // The selected source grid also applies at native resolution. Stencil-tagged
    // overlays bypass spatial reconstruction and temporal blending.
    vec3 currentColor = nativeResolution || isPostTemporalForward
        ? currentColorRaw
        : mix(currentColorRaw, currentColorFiltered, 0.25f);
#endif
    vec3 currentYCoCg = TsrRgbToYCoCg(currentColor);
    float currentLuma = currentYCoCg.x;

    // Neighborhood in YCoCg at source resolution
    vec3 minBound, maxBound, meanYCoCg, sampleMin, sampleMax;
    ComputeNeighborhoodBounds(uv, minBound, maxBound, meanYCoCg, sampleMin, sampleMax);
    float historyAge = 0.0;

    float currentDepth = texture(DepthView, uv).r;
    vec3 historyYCoCg = currentYCoCg;
    bool canUseHistory = TsrCanSampleHistory(HistoryReady, historyUV);
    canUseHistory = canUseHistory && !isPostTemporalForward
        && TsrIsValidUv(sourceUv) && TsrIsValidUv(historyDepthUv);
#ifdef XR_TSR_STABLE_OUTPUT
    bool historyAllowed = canUseHistory;
    float surfaceSupport = 0.0;
    float rejectionReason = 1.0;
#endif

    if (canUseHistory)
    {
#ifdef XR_TSR_STABLE_OUTPUT
        vec3 historyRGB;
        canUseHistory = TsrSampleSurfaceHistory(surface, historyUV,
            currentJitter, previousJitter, historyRGB, surfaceSupport, historyAge, rejectionReason);
        if (canUseHistory)
            historyYCoCg = TsrRgbToYCoCg(historyRGB);
#else
        vec2 historySampleUv = ClampHistoryUv(historyUV);
        float historyDepth = texture(HistoryDepth,
            ClampUvToTexels(historyDepthUv, TextureTexelSize(HistoryDepth))).r;
        if (TsrDepthMatches(currentDepth, historyDepth, DepthRejectThreshold))
        {
            // Bicubic Catmull-Rom on full-resolution history
            vec3 historyRGB = SampleCatmullRom(TsrHistoryColor, historySampleUv, historyTexelSize);
            historyYCoCg = TsrRgbToYCoCg(historyRGB);
        }
        else
        {
            canUseHistory = false;
        }
#endif
    }

    // Mature, surface-validated detail may use the observed neighborhood range.
    // A rare thin foreground sample otherwise falls outside mean +/- sigma.
#ifdef XR_TSR_STABLE_OUTPUT
    float canonicalReactive = clamp(texture(AdvancedReactiveMask, uv).r, 0.0, 1.0);
    TsrCoverageResult coverage = TsrResolveCoverage(uv, historyUV, currentJitter, previousJitter,
        historyAllowed, isPostTemporalForward ? 1.0 : canonicalReactive,
        TsrComputeMotionMask(velocity, ReactiveVelocityScale), currentLuma, depthDiscontinuity);
    if (coverage.historyValid)
    {
        canUseHistory = true;
        historyAge = coverage.historyAge;
        historyYCoCg = TsrRgbToYCoCg(coverage.historyColor);
    }
    else if (coverage.currentCoverage >= 0.0)
    {
        // Seed color and coverage together. An ordinary history mixture has
        // no matching coverage state and cannot be relabeled as this frame.
        canUseHistory = false;
        historyAge = 0.0;
        historyYCoCg = currentYCoCg;
    }
    float detailProtection = TsrComputeDetailProtection(canUseHistory, historyAge,
        TsrComputeMotionMask(velocity, ReactiveVelocityScale), canonicalReactive);
    minBound = mix(minBound, min(minBound, sampleMin), detailProtection);
    maxBound = mix(maxBound, max(maxBound, sampleMax), detailProtection);
    minBound = mix(minBound, min(minBound, coverage.colorMin), coverage.retention);
    maxBound = mix(maxBound, max(maxBound, coverage.colorMax), coverage.retention);
#endif
    // Clip toward AABB center
    vec3 clippedHistory = TsrClipHistoryToNeighborhood(historyYCoCg, minBound, maxBound);
    float historyLuma = clippedHistory.x;

    float motionMask = TsrComputeMotionMask(velocity, ReactiveVelocityScale);
    float geometryInstability = TsrComputeGeometryInstability(depthDiscontinuity, motionMask);

    // Reactive mask
    vec4 currentSample = texture(PostProcessOutputTexture, uv);
    float reactiveMask = TsrComputeReactiveMask(
        currentSample.a,
        currentLuma,
        historyLuma,
        motionMask,
        ReactiveTransparencyRange,
        ReactiveLumaThreshold);
#ifdef XR_ADVANCED_REACTIVE_MASK
    reactiveMask = max(reactiveMask, clamp(texture(AdvancedReactiveMask, uv).r, 0.0, 1.0));
#endif
    float confidence = TsrComputeConfidence(
        geometryInstability,
        reactiveMask,
        motionMask,
        ConfidencePower);
    float historyWeight = TsrComputeHistoryWeight(
        canUseHistory,
        confidence,
        FeedbackMin,
        FeedbackMax);
    if (isPostTemporalForward)
        historyWeight = 0.0f;

#ifdef XR_TSR_STABLE_OUTPUT
    historyWeight = mix(historyWeight, max(historyWeight, clamp(FeedbackMax, 0.0, 0.99)), coverage.retention);
    OutHistoryMetadata = vec4(TsrAdvanceHistoryAge(canUseHistory, historyAge, reactiveMask),
        coverage.historyValid ? mix(coverage.currentCoverage, coverage.historyCoverage, historyWeight)
            : coverage.currentCoverage,
        TsrCompressLuma(currentLuma), coverage.flicker);
#endif

    vec3 resolved = mix(currentYCoCg, clippedHistory, historyWeight);
    vec3 result = TsrYCoCgToRgb(resolved);
#ifdef XR_TSR_STABLE_OUTPUT
    // Persist the real, unsharpened resolve even while viewing diagnostics.
    OutAccumulation = vec4(max(result, vec3(0.0)), 1.0);
#endif
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
#ifdef XR_TSR_STABLE_OUTPUT
            case 6:
                debugColor = vec3(rejectionReason * 0.25, surfaceSupport, coverage.historyValid ? 1.0 : 0.0);
                break;
            case 7:
                debugColor = coverage.currentCoverage < 0.0 ? vec3(0.0)
                    : vec3(coverage.currentCoverage, OutHistoryMetadata.y, coverage.failureReason / 7.0);
                break;
            case 8:
                debugColor = vec3(abs(coverage.flicker), coverage.retention, coverage.flicker < 0.0 ? 1.0 : 0.0);
                break;
            case 9:
                debugColor = abs(historyYCoCg - clippedHistory) * 4.0;
                break;
#endif
        }

        OutColor = vec4(debugColor, 1.0f);
        return;
    }

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
        float sharpenStrength = TsrComputeSharpenStrength(
            nativeResolution,
            historyWeight,
            reactiveMask);
#ifdef XR_TSR_STABLE_OUTPUT
        sharpenStrength *= TsrComputeSharpenStability(canUseHistory, historyAge,
            geometryInstability, motionMask, reactiveMask,
            currentLuma, historyYCoCg.x, ReactiveLumaThreshold);
        sharpenStrength *= 1.0 - coverage.retention;
#endif
        result += highFreq * sharpenStrength;
    }

    OutColor = vec4(max(result, vec3(0.0f)), 1.0f);
}
