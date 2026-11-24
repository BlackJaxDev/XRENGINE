#version 460
#extension GL_OVR_multiview2 : require
#define XR_TSR_STEREO 1

#pragma snippet "ScreenSpaceUtils"
#pragma snippet "TemporalSuperResolutionCore"

layout(location = 0) out vec4 OutColor;
#if defined(XR_TSR_STABLE_OUTPUT) && !defined(XR_TSR_EDGE_MASK_PASS)
layout(location = 1) out vec4 OutHistoryMetadata;
layout(location = 2) out vec4 OutAccumulation;
#endif
#ifdef XR_TSR_STABLE_OUTPUT
uniform sampler2DArray TsrHistoryMetadata;
#endif
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray PostProcessOutputTexture;
uniform sampler2DArray Velocity;
uniform sampler2DArray DepthView;
uniform sampler2DArray HistoryDepth;
uniform sampler2DArray TsrHistoryColor;
// Editor gizmos have no valid temporal history. Scene stencil is independent.
#ifdef XR_EDITOR_HIGHLIGHT_MODULE
uniform sampler2DArray EditorHighlightMask;
#elif !defined(XR_SCENE_STENCIL_AUTHORED)
uniform usampler2DArray StencilView;
#endif

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
uniform int AntiAliasingFilterMode;
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

// Integrates a 2x2 output-pixel bilinear tent kernel W(x / r) = 1 - |x / r| across
// the overlapping 3x3 source texels of the active multiview layer.
vec3 SampleTent2x2(sampler2DArray tex, vec2 uv, vec2 radiusInSourceTexels)
{
    ivec2 extent = max(textureSize(tex, 0).xy, ivec2(1));
    int layer = int(gl_ViewID_OVR);
    vec2 radius = clamp(radiusInSourceTexels, vec2(0.25), vec2(1.0));
    vec2 invRadius = 1.0 / radius;
    vec2 position = clamp(uv, vec2(0.0), vec2(1.0)) * vec2(extent);
    ivec2 centerPixel = ivec2(floor(position));
    float wx[3];
    float wy[3];
    for (int i = -1; i <= 1; ++i)
    {
        wx[i + 1] = TsrTent1DIntegral((float(centerPixel.x + i) - position.x) * invRadius.x,
            (float(centerPixel.x + i + 1) - position.x) * invRadius.x);
        wy[i + 1] = TsrTent1DIntegral((float(centerPixel.y + i) - position.y) * invRadius.y,
            (float(centerPixel.y + i + 1) - position.y) * invRadius.y);
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

float SamplePostTemporalForwardMask(vec2 uv)
{
#ifdef XR_EDITOR_HIGHLIGHT_MODULE
    return step(0.5, texture(EditorHighlightMask, EyeUv(clamp(uv, vec2(0.0), vec2(1.0)))).b);
#elif !defined(XR_SCENE_STENCIL_AUTHORED)
    uint stencilBits = texture(StencilView, EyeUv(clamp(uv, vec2(0.0), vec2(1.0)))).r;
    return ((stencilBits & 0x80u) != 0u) ? 1.0 : 0.0;
#else
    return 0.0;
#endif
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
            vec3 s = TsrRgbToYCoCg(TsrCompressHdrColor(texture(PostProcessOutputTexture, EyeUv(sampleUv)).rgb));
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
#ifdef XR_TSR_EDGE_MASK
// Edge mask of this frame, written at internal resolution by the edge-mask pass.
uniform sampler2DArray TsrEdgeMask;
// True when the internal pixel that TsrBuildCoveragePair uses as its 3x3 center for sourceUv
// passed the current-frame center classification. Out-of-range centers fail that classification too.
bool TsrCurrentEdgeMaskSet(vec2 sourceUv)
{
    ivec2 extent = textureSize(DepthView, 0).xy;
    ivec2 center = ivec2(sourceUv * vec2(extent));
    if (any(lessThan(center, ivec2(0))) || any(greaterThanEqual(center, extent)))
        return false;
    return texelFetch(TsrEdgeMask, ivec3(center, int(gl_ViewID_OVR)), 0).r > 0.5;
}
#endif
#pragma snippet "TemporalSuperResolutionSurface"
#pragma snippet "TemporalSuperResolutionCoverage"

#ifdef XR_TSR_EDGE_MASK_PASS
// Edge-mask pass at internal resolution. Each fragment is one internal pixel. It runs the
// current-frame coverage classification with that pixel as the 3x3 center. With the pixel-center
// UV the footprint loop of TsrBuildCoveragePair holds one sample (the center), so the result is
// exactly the center-dependent part of the classification: depth clusters, counts, connectivity,
// reactivity and color contrast. The classify pass marks the output pixels whose 3x3 center has
// the mask set; only those pixels run the full resolve, where TsrBuildCoveragePair can succeed.
void main()
{
    ivec2 extent = textureSize(DepthView, 0).xy;
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    if (any(greaterThanEqual(pixel, extent)))
        discard;
    TsrCoveragePair pair;
    bool edge = TsrBuildCoveragePair((vec2(pixel) + 0.5) / vec2(extent), false, pair);
    OutColor = vec4(edge ? 1.0 : 0.0, 0.0, 0.0, 1.0);
}
#elif defined(XR_TSR_EDGE_CLASSIFY)
// Edge stencil classify pass at output resolution. It maps the output pixel to the TSR source UV
// exactly as the resolve does and keeps the fragment only when the edge mask is set for that UV's
// 3x3 center. The material writes the edge stencil bit for kept fragments and no color.
void main()
{
    vec2 outputUv = XRENGINE_FramebufferUV(
        gl_FragCoord.xy, ScreenOrigin, vec2(ScreenWidth, ScreenHeight));
    vec2 currentJitter = TsrFramebufferUvDisplacement(CurrentJitterUv, FramebufferTextureYDirection);
    if (gl_ViewID_OVR != 0u)
        currentJitter = TsrFramebufferUvDisplacement(CurrentJitterUvRight, FramebufferTextureYDirection);
    vec2 uv = ClampSourceUv(outputUv + currentJitter);
    if (!TsrCurrentEdgeMaskSet(uv))
        discard;
    OutColor = vec4(0.0);
}
#else
void main()
{
#ifndef XR_TSR_STENCIL_SPLIT
    // The split resolves keep early stencil rejection, so they do not discard. Fragments outside
    // the viewport are never generated for them.
    vec2 clipXY = FragPos.xy;
    if (clipXY.x < -1.0 || clipXY.x > 1.0 || clipXY.y < -1.0 || clipXY.y > 1.0)
        discard;
#endif

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
    ivec2 sourceExtent = max(textureSize(PostProcessOutputTexture, 0).xy, ivec2(1));
    ivec2 historyExtent = max(textureSize(TsrHistoryColor, 0).xy, ivec2(1));
    bool nativeResolution = all(equal(sourceExtent, historyExtent));
    vec2 tentRadius = clamp(vec2(sourceExtent) / vec2(historyExtent), vec2(0.25), vec2(1.0));

    vec4 currentSample = texture(PostProcessOutputTexture, EyeUv(uv));
    vec3 currentColorRaw = currentSample.rgb;
#ifdef XR_TSR_STABLE_OUTPUT
    // Surface ownership and current color use positive taps; Tent2x2 reconstructs
    // across a 2x2 output-pixel tent footprint scaled into source texel space.
    vec3 currentColor = (AntiAliasingFilterMode == 2 && !isPostTemporalForward)
        ? SampleTent2x2(PostProcessOutputTexture, uv, tentRadius)
        : currentColorRaw;
#else
    // The selected source grid also applies at native resolution. Stencil-tagged
    // overlays bypass spatial reconstruction and temporal blending.
    vec3 currentColor;
    if (isPostTemporalForward || AntiAliasingFilterMode == 1)
        currentColor = currentColorRaw;
    else if (AntiAliasingFilterMode == 2)
        currentColor = SampleTent2x2(PostProcessOutputTexture, uv, tentRadius);
    else
        currentColor = nativeResolution
            ? currentColorRaw
            : mix(currentColorRaw, SampleCurrentReconstruction(PostProcessOutputTexture, uv, sourceTexelSize), 0.25);
#endif
    vec3 currentYCoCg = TsrRgbToYCoCg(TsrCompressHdrColor(currentColor));
    float currentLuma = TsrRgbToYCoCg(max(currentColor, vec3(0.0))).x;

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
            historyYCoCg = TsrRgbToYCoCg(TsrCompressHdrColor(historyRGB));
#else
        vec2 historySampleUv = ClampHistoryUv(historyUV);
        float historyDepth = texture(HistoryDepth,
            EyeUv(ClampUvToTexels(historyDepthUv, TextureTexelSize(HistoryDepth)))).r;
        if (TsrDepthMatches(currentDepth, historyDepth, DepthRejectThreshold))
        {
            vec3 historyRgb;
            if (AntiAliasingFilterMode == 1)
                historyRgb = texture(TsrHistoryColor, EyeUv(historySampleUv)).rgb;
            else
                // Keep history sharp with bicubic Catmull-Rom under Default and Tent2x2
                // so temporal feedback does not compound tent blur across frames.
                historyRgb = SampleCatmullRom(TsrHistoryColor, historySampleUv, historyTexelSize);
            historyYCoCg = TsrRgbToYCoCg(TsrCompressHdrColor(historyRgb));
        }
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
    bool writeCoverageState = false;
    if (coverage.historyValid)
    {
        canUseHistory = true;
        historyAge = coverage.historyAge;
        historyYCoCg = TsrRgbToYCoCg(TsrCompressHdrColor(coverage.historyColor));
        writeCoverageState = true;
    }
    else if (!canUseHistory && coverage.currentCoverage >= 0.0)
    {
        // Seed coverage metadata only when ordinary surface history is unavailable.
        // Never discard a validated surface history or mark it as unvalidated coverage.
        historyAge = 0.0;
        historyYCoCg = currentYCoCg;
        writeCoverageState = true;
    }
    float detailProtection = TsrComputeDetailProtection(canUseHistory, historyAge,
        TsrComputeMotionMask(velocity, ReactiveVelocityScale), canonicalReactive);
    minBound = mix(minBound, min(minBound, sampleMin), detailProtection);
    maxBound = mix(maxBound, max(maxBound, sampleMax), detailProtection);
    minBound = mix(minBound, min(minBound, coverage.colorMin), coverage.retention);
    maxBound = mix(maxBound, max(maxBound, coverage.colorMax), coverage.retention);
#endif
    vec3 clippedHistory = TsrClipHistoryToNeighborhood(historyYCoCg, minBound, maxBound);
    float historyLuma = TsrRgbToYCoCg(TsrExpandHdrColor(TsrYCoCgToRgb(clippedHistory))).x;
    float motionMask = TsrComputeMotionMask(velocity, ReactiveVelocityScale);
    float geometryInstability = TsrComputeGeometryInstability(depthDiscontinuity, motionMask);
    float reactiveMask = TsrComputeReactiveMask(
        currentSample.a,
        currentLuma,
        historyLuma,
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
            : (writeCoverageState ? coverage.currentCoverage : -1.0),
        TsrCompressLuma(currentLuma), coverage.flicker);
#endif

    vec3 result = TsrExpandHdrColor(TsrYCoCgToRgb(mix(currentYCoCg, clippedHistory, historyWeight)));
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

#ifndef XR_TSR_STABLE_OUTPUT
    if (AntiAliasingFilterMode == 0 && postTemporalCoverage <= 0.0)
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
        result += highFreq * sharpenStrength;
    }
#endif

    OutColor = vec4(max(result, vec3(0.0)), 1.0);
}
#endif
