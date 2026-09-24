#version 460
#extension GL_OVR_multiview2 : require
#define XR_TSR_STEREO 1

#pragma snippet "ScreenSpaceUtils"
#pragma snippet "TemporalSuperResolutionCore"

layout(location = 0) out vec4 OutColor;
#ifdef XR_TSR_STABLE_OUTPUT
layout(location = 1) out vec4 OutHistoryMetadata;
layout(location = 2) out vec4 OutAccumulation;
uniform sampler2DArray TsrHistoryMetadata;
#endif
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray PostProcessOutputTexture;
uniform sampler2DArray Velocity;
uniform sampler2DArray DepthView;
uniform sampler2DArray HistoryDepth;
uniform sampler2DArray TsrHistoryColor;
uniform usampler2DArray StencilView;

#ifdef XR_ADVANCED_REACTIVE_MASK
// Canonical opaque/late reactivity is independent of final output alpha.
uniform sampler2DArray AdvancedReactiveMask;
#endif

uniform bool HistoryReady;
uniform vec2 SourceTexelSize;
uniform vec2 HistoryTexelSize;
uniform vec2 CurrentJitterUv;
uniform vec2 PreviousJitterUv;
#ifdef XR_TSR_STABLE_OUTPUT
uniform vec2 CurrentJitterUvRight;
uniform vec2 PreviousJitterUvRight;
#endif
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
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform vec2 ScreenOrigin;

vec3 EyeUv(vec2 uv)
{
    return vec3(uv, float(gl_ViewID_OVR));
}

vec2 TextureTexelSize(sampler2DArray tex)
{
    ivec3 size = textureSize(tex, 0);
    return 1.0 / vec2(max(size.xy, ivec2(1)));
}

vec2 ClampUvToTexels(vec2 uv, vec2 texelSize)
{
    return TsrClampUvToTexels(uv, texelSize);
}

vec2 ClampSourceUv(vec2 uv)
{
    return ClampUvToTexels(uv, TextureTexelSize(PostProcessOutputTexture));
}

vec2 ClampHistoryUv(vec2 uv)
{
    return ClampUvToTexels(uv, TextureTexelSize(TsrHistoryColor));
}

vec3 SampleCatmullRom(sampler2DArray tex, vec2 uv, vec2 texelSize)
{
    uv = ClampUvToTexels(uv, texelSize);

    vec2 texSize = 1.0 / texelSize;
    vec2 position = uv * texSize;
    vec2 center = floor(position - 0.5) + 0.5;
    vec2 f = position - center;
    vec2 f2 = f * f;
    vec2 f3 = f2 * f;

    vec2 w0 = -0.5 * f3 + f2 - 0.5 * f;
    vec2 w1 =  1.5 * f3 - 2.5 * f2 + 1.0;
    vec2 w2 = -1.5 * f3 + 2.0 * f2 + 0.5 * f;
    vec2 w3 =  0.5 * f3 - 0.5 * f2;

    vec2 w12 = w1 + w2;
    vec2 tc12 = (center + w2 / w12) * texelSize;
    vec2 tc0 = (center - 1.0) * texelSize;
    vec2 tc3 = (center + 2.0) * texelSize;
    float eye = float(gl_ViewID_OVR);

    vec3 result =
        texture(tex, vec3(ClampUvToTexels(vec2(tc12.x, tc12.y), texelSize), eye)).rgb * (w12.x * w12.y) +
        texture(tex, vec3(ClampUvToTexels(vec2(tc0.x,  tc12.y), texelSize), eye)).rgb * (w0.x  * w12.y) +
        texture(tex, vec3(ClampUvToTexels(vec2(tc3.x,  tc12.y), texelSize), eye)).rgb * (w3.x  * w12.y) +
        texture(tex, vec3(ClampUvToTexels(vec2(tc12.x, tc0.y ), texelSize), eye)).rgb * (w12.x * w0.y ) +
        texture(tex, vec3(ClampUvToTexels(vec2(tc12.x, tc3.y ), texelSize), eye)).rgb * (w12.x * w3.y );

    float totalWeight = (w12.x * w12.y) + (w0.x * w12.y) + (w3.x * w12.y)
        + (w12.x * w0.y) + (w12.x * w3.y);
    return result / max(totalWeight, 1e-6);
}

vec3 SampleCurrentReconstruction(sampler2DArray tex, vec2 uv, vec2 texelSize)
{
    uv = ClampUvToTexels(uv, texelSize);
    vec3 center = texture(tex, EyeUv(uv)).rgb * 4.0;
    vec3 axial =
        texture(tex, EyeUv(ClampUvToTexels(uv + vec2(-1.0, 0.0) * texelSize, texelSize))).rgb +
        texture(tex, EyeUv(ClampUvToTexels(uv + vec2( 1.0, 0.0) * texelSize, texelSize))).rgb +
        texture(tex, EyeUv(ClampUvToTexels(uv + vec2(0.0, -1.0) * texelSize, texelSize))).rgb +
        texture(tex, EyeUv(ClampUvToTexels(uv + vec2(0.0,  1.0) * texelSize, texelSize))).rgb;
    vec3 diagonal =
        texture(tex, EyeUv(ClampUvToTexels(uv + vec2(-1.0, -1.0) * texelSize, texelSize))).rgb +
        texture(tex, EyeUv(ClampUvToTexels(uv + vec2( 1.0, -1.0) * texelSize, texelSize))).rgb +
        texture(tex, EyeUv(ClampUvToTexels(uv + vec2(-1.0,  1.0) * texelSize, texelSize))).rgb +
        texture(tex, EyeUv(ClampUvToTexels(uv + vec2( 1.0,  1.0) * texelSize, texelSize))).rgb;
    return (center + axial * 2.0 + diagonal) * (1.0 / 16.0);
}

float SamplePostTemporalForwardMask(vec2 uv)
{
    uint stencilBits = texture(StencilView, EyeUv(clamp(uv, vec2(0.0), vec2(1.0)))).r;
    return ((stencilBits & 0x80u) != 0u) ? 1.0 : 0.0;
}

vec2 FindClosestVelocity(vec2 uv)
{
    vec2 closestOffset = vec2(0.0);
    float closestDepth = 1e20;
    vec2 depthTexelSize = TextureTexelSize(DepthView);

    for (int x = -1; x <= 1; ++x)
    {
        for (int y = -1; y <= 1; ++y)
        {
            vec2 sampleUv = ClampSourceUv(uv + vec2(float(x), float(y)) * depthTexelSize);
            float depth = texture(DepthView, EyeUv(sampleUv)).r;
            if (depth < closestDepth)
            {
                closestDepth = depth;
                closestOffset = sampleUv - uv;
            }
        }
    }

    return texture(Velocity, EyeUv(ClampSourceUv(uv + closestOffset))).xy;
}

void ComputeNeighborhoodBounds(vec2 uv, out vec3 minColor, out vec3 maxColor,
    out vec3 meanColor, out vec3 sampleMin, out vec3 sampleMax)
{
    sampleMin = vec3(1e20);
    sampleMax = vec3(-1e20);
    vec3 m1 = vec3(0.0);
    vec3 m2 = vec3(0.0);
    vec2 sourceTexelSize = TextureTexelSize(PostProcessOutputTexture);

    for (int x = -1; x <= 1; ++x)
    {
        for (int y = -1; y <= 1; ++y)
        {
            vec2 sampleUv = ClampSourceUv(uv + vec2(float(x), float(y)) * sourceTexelSize);
            vec3 s = TsrRgbToYCoCg(texture(PostProcessOutputTexture, EyeUv(sampleUv)).rgb);
            sampleMin = min(sampleMin, s);
            sampleMax = max(sampleMax, s);
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
    uv = ClampSourceUv(uv);
    vec2 depthTexelSize = TextureTexelSize(DepthView);
    float centerDepth = texture(DepthView, EyeUv(uv)).r;
    float minDepth = centerDepth;
    float maxDepth = centerDepth;
    const vec2 offsets[4] = vec2[](vec2(1.0, 0.0), vec2(-1.0, 0.0), vec2(0.0, 1.0), vec2(0.0, -1.0));
    for (int i = 0; i < 4; ++i)
    {
        float d = texture(DepthView, EyeUv(ClampSourceUv(uv + offsets[i] * depthTexelSize))).r;
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

#ifdef XR_TSR_STABLE_OUTPUT
float TsrLoadCurrentDepth(ivec2 pixel) { return texelFetch(DepthView, ivec3(pixel, int(gl_ViewID_OVR)), 0).r; }
vec2 TsrLoadVelocity(ivec2 pixel) { return texelFetch(Velocity, ivec3(pixel, int(gl_ViewID_OVR)), 0).xy; }
float TsrLoadHistoryDepth(ivec2 pixel) { return texelFetch(HistoryDepth, ivec3(pixel, int(gl_ViewID_OVR)), 0).r; }
vec4 TsrLoadHistoryColor(ivec2 pixel) { return texelFetch(TsrHistoryColor, ivec3(pixel, int(gl_ViewID_OVR)), 0); }
vec4 TsrLoadHistoryMetadata(ivec2 pixel) { return texelFetch(TsrHistoryMetadata, ivec3(pixel, int(gl_ViewID_OVR)), 0); }
vec3 TsrLoadCurrentColor(ivec2 pixel) { return texelFetch(PostProcessOutputTexture, ivec3(pixel, int(gl_ViewID_OVR)), 0).rgb; }
float TsrLoadCurrentReactivity(ivec2 pixel) { return texelFetch(AdvancedReactiveMask, ivec3(pixel, int(gl_ViewID_OVR)), 0).r; }
#endif
#pragma snippet "TemporalSuperResolutionSurface"
#pragma snippet "TemporalSuperResolutionCoverage"

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x < -1.0 || clipXY.x > 1.0 || clipXY.y < -1.0 || clipXY.y > 1.0)
        discard;

    vec2 outputUv = XRENGINE_FramebufferUV(
        gl_FragCoord.xy, ScreenOrigin, vec2(ScreenWidth, ScreenHeight));
    vec2 currentJitter = TsrFramebufferUvDisplacement(CurrentJitterUv, FramebufferTextureYDirection);
    vec2 previousJitter = TsrFramebufferUvDisplacement(PreviousJitterUv, FramebufferTextureYDirection);
#ifdef XR_TSR_STABLE_OUTPUT
    if (gl_ViewID_OVR != 0u)
    {
        currentJitter = TsrFramebufferUvDisplacement(CurrentJitterUvRight, FramebufferTextureYDirection);
        previousJitter = TsrFramebufferUvDisplacement(PreviousJitterUvRight, FramebufferTextureYDirection);
    }
    // Advanced draws its scene and overlays before removing projection jitter.
    vec2 sourceUv = outputUv + currentJitter;
#else
    // Default composites unjittered overlays before TSR and retains its current grid.
    vec2 sourceUv = outputUv;
#endif
    vec2 uv = ClampSourceUv(sourceUv);
    float postTemporalCoverage = SamplePostTemporalForwardMask(uv);
    bool isPostTemporalForward = postTemporalCoverage > 0.5;

    float depthDiscontinuity = EvaluateDepthDiscontinuity(uv);
#ifdef XR_TSR_STABLE_OUTPUT
    TsrSurfaceSample surface = TsrSelectSurface(uv);
    vec2 velocity = surface.velocity;
#else
    vec2 velocity = texture(Velocity, EyeUv(uv)).xy;
    if (depthDiscontinuity > 1e-4)
        velocity = FindClosestVelocity(uv);
#endif

    if (isPostTemporalForward)
        velocity = vec2(0.0);

#ifdef XR_TSR_STABLE_OUTPUT
    // Color history is stable; its raw depth companion retains previous jitter.
    vec2 historyUV = outputUv - TsrFramebufferUvDisplacement(velocity * 0.5, FramebufferTextureYDirection);
    vec2 historyDepthUv = historyUV + previousJitter;
#else
    vec2 historyUV = uv - TsrFramebufferUvDisplacement(velocity * 0.5, FramebufferTextureYDirection)
        + previousJitter - currentJitter;
    vec2 historyDepthUv = historyUV;
#endif
    vec2 sourceTexelSize = TextureTexelSize(PostProcessOutputTexture);
    vec2 historyTexelSize = TextureTexelSize(TsrHistoryColor);
    bool nativeResolution = all(equal(
        textureSize(PostProcessOutputTexture, 0).xy,
        textureSize(TsrHistoryColor, 0).xy));

    vec4 currentSample = texture(PostProcessOutputTexture, EyeUv(uv));
    vec3 currentColorRaw = currentSample.rgb;
#ifdef XR_TSR_STABLE_OUTPUT
    // Surface ownership and current color use the same positive bilinear taps.
    vec3 currentColor = currentColorRaw;
#else
    // The selected source grid also applies at native resolution. Stencil-tagged
    // overlays bypass spatial reconstruction and temporal blending.
    vec3 currentColor = nativeResolution || isPostTemporalForward
        ? currentColorRaw
        : mix(currentColorRaw, SampleCurrentReconstruction(PostProcessOutputTexture, uv, sourceTexelSize), 0.25);
#endif
    vec3 currentYCoCg = TsrRgbToYCoCg(currentColor);
    float currentLuma = currentYCoCg.x;

    vec3 minBound;
    vec3 maxBound;
    vec3 meanYCoCg;
    vec3 sampleMin, sampleMax;
    ComputeNeighborhoodBounds(uv, minBound, maxBound, meanYCoCg, sampleMin, sampleMax);
    float historyAge = 0.0;

    float currentDepth = texture(DepthView, EyeUv(uv)).r;
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
            EyeUv(ClampUvToTexels(historyDepthUv, TextureTexelSize(HistoryDepth)))).r;
        if (TsrDepthMatches(currentDepth, historyDepth, DepthRejectThreshold))
            historyYCoCg = TsrRgbToYCoCg(SampleCatmullRom(TsrHistoryColor, historySampleUv, historyTexelSize));
        else
            canUseHistory = false;
#endif
    }

    // Preserve mature, surface-validated rare detail within the observed range.
#ifdef XR_TSR_STABLE_OUTPUT
    float canonicalReactive = clamp(texture(AdvancedReactiveMask, EyeUv(uv)).r, 0.0, 1.0);
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
    vec3 clippedHistory = TsrClipHistoryToNeighborhood(historyYCoCg, minBound, maxBound);
    float motionMask = TsrComputeMotionMask(velocity, ReactiveVelocityScale);
    float geometryInstability = TsrComputeGeometryInstability(depthDiscontinuity, motionMask);
    float reactiveMask = TsrComputeReactiveMask(
        currentSample.a,
        currentLuma,
        clippedHistory.x,
        motionMask,
        ReactiveTransparencyRange,
        ReactiveLumaThreshold);
#ifdef XR_ADVANCED_REACTIVE_MASK
    reactiveMask = max(reactiveMask, clamp(texture(AdvancedReactiveMask, EyeUv(uv)).r, 0.0, 1.0));
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
    if (postTemporalCoverage > 0.5)
        historyWeight = 0.0;

#ifdef XR_TSR_STABLE_OUTPUT
    historyWeight = mix(historyWeight, max(historyWeight, clamp(FeedbackMax, 0.0, 0.99)), coverage.retention);
    OutHistoryMetadata = vec4(TsrAdvanceHistoryAge(canUseHistory, historyAge, reactiveMask),
        coverage.historyValid ? mix(coverage.currentCoverage, coverage.historyCoverage, historyWeight)
            : coverage.currentCoverage,
        TsrCompressLuma(currentLuma), coverage.flicker);
#endif

    vec3 result = TsrYCoCgToRgb(mix(currentYCoCg, clippedHistory, historyWeight));
#ifdef XR_TSR_STABLE_OUTPUT
    // Persist the real, unsharpened resolve even while viewing diagnostics.
    OutAccumulation = vec4(max(result, vec3(0.0)), 1.0);
#endif
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
#ifdef XR_TSR_STABLE_OUTPUT
        else if (DebugMode == 6)
            debugColor = vec3(rejectionReason * 0.25, surfaceSupport, coverage.historyValid ? 1.0 : 0.0);
        else if (DebugMode == 7)
            debugColor = coverage.currentCoverage < 0.0 ? vec3(0.0)
                : vec3(coverage.currentCoverage, OutHistoryMetadata.y, coverage.failureReason / 7.0);
        else if (DebugMode == 8)
            debugColor = vec3(abs(coverage.flicker), coverage.retention, coverage.flicker < 0.0 ? 1.0 : 0.0);
        else if (DebugMode == 9)
            debugColor = abs(historyYCoCg - clippedHistory) * 4.0;
#endif
        OutColor = vec4(debugColor, 1.0);
        return;
    }

    if (postTemporalCoverage <= 0.0)
    {
        vec3 neighbors =
            texture(PostProcessOutputTexture, EyeUv(ClampSourceUv(uv + vec2(-1.0, 0.0) * sourceTexelSize))).rgb +
            texture(PostProcessOutputTexture, EyeUv(ClampSourceUv(uv + vec2( 1.0, 0.0) * sourceTexelSize))).rgb +
            texture(PostProcessOutputTexture, EyeUv(ClampSourceUv(uv + vec2(0.0, -1.0) * sourceTexelSize))).rgb +
            texture(PostProcessOutputTexture, EyeUv(ClampSourceUv(uv + vec2(0.0,  1.0) * sourceTexelSize))).rgb;
        vec3 highFreq = currentColorRaw - neighbors * 0.25;
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

    OutColor = vec4(max(result, vec3(0.0)), 1.0);
}
