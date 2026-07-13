// Sampling remains texture-type-specific in the mono and multiview entry
// points. History rejection, neighborhood clamping, confidence, and sharpening
// live here so their numerical policy cannot drift between those entry points.

vec3 TsrRgbToYCoCg(vec3 rgb)
{
    float y = dot(rgb, vec3(0.25, 0.5, 0.25));
    float co = dot(rgb, vec3(0.5, 0.0, -0.5));
    float cg = dot(rgb, vec3(-0.25, 0.5, -0.25));
    return vec3(y, co, cg);
}

vec3 TsrYCoCgToRgb(vec3 ycocg)
{
    float y = ycocg.x;
    float co = ycocg.y;
    float cg = ycocg.z;
    return vec3(y + co - cg, y + cg, y - co - cg);
}

bool TsrIsValidUv(vec2 uv)
{
    return all(greaterThanEqual(uv, vec2(0.0)))
        && all(lessThanEqual(uv, vec2(1.0)));
}

vec2 TsrClampUvToTexels(vec2 uv, vec2 texelSize)
{
    vec2 halfTexel = texelSize * 0.5;
    return clamp(uv, halfTexel, vec2(1.0) - halfTexel);
}

bool TsrCanSampleHistory(bool historyReady, vec2 historyUv)
{
    return historyReady && TsrIsValidUv(historyUv);
}

bool TsrDepthMatches(float currentDepth, float historyDepth, float rejectThreshold)
{
    return abs(historyDepth - currentDepth) <= rejectThreshold;
}

vec3 TsrClipHistoryToNeighborhood(vec3 history, vec3 neighborhoodMin, vec3 neighborhoodMax)
{
    vec3 center = 0.5 * (neighborhoodMin + neighborhoodMax);
    vec3 extents = 0.5 * (neighborhoodMax - neighborhoodMin) + 1e-5;
    vec3 offset = history - center;
    vec3 axes = abs(extents / max(abs(offset), vec3(1e-5)));
    float scale = clamp(min(axes.x, min(axes.y, axes.z)), 0.0, 1.0);
    return center + offset * scale;
}

float TsrComputeMotionMask(vec2 velocity, float reactiveVelocityScale)
{
    float motionStart = max(reactiveVelocityScale * 0.05, 0.0005);
    return smoothstep(motionStart, reactiveVelocityScale, length(velocity));
}

float TsrComputeGeometryInstability(float depthDiscontinuity, float motionMask)
{
    return clamp(depthDiscontinuity * mix(0.2, 1.0, motionMask), 0.0, 1.0);
}

float TsrComputeReactiveMask(
    float currentAlpha,
    float currentLuma,
    float historyLuma,
    float motionMask,
    vec2 transparencyRange,
    float reactiveLumaThreshold)
{
    float transparencyMask = smoothstep(
        transparencyRange.x,
        transparencyRange.y,
        1.0 - clamp(currentAlpha, 0.0, 1.0)) * max(0.15, motionMask);
    float luminanceDelta = abs(currentLuma - historyLuma);
    float luminanceMask = smoothstep(
        0.25 * reactiveLumaThreshold,
        reactiveLumaThreshold,
        luminanceDelta) * motionMask;
    return clamp(max(transparencyMask, max(motionMask, luminanceMask)), 0.0, 1.0);
}

float TsrComputeConfidence(
    float geometryInstability,
    float reactiveMask,
    float motionMask,
    float confidencePower)
{
    float confidence = pow(
        clamp((1.0 - geometryInstability) * (1.0 - reactiveMask), 0.0, 1.0),
        confidencePower);
    float staticConfidenceFloor = (1.0 - motionMask) * (1.0 - reactiveMask) * 0.5;
    return max(confidence, staticConfidenceFloor);
}

float TsrComputeHistoryWeight(
    bool canUseHistory,
    float confidence,
    float feedbackMin,
    float feedbackMax)
{
    return canUseHistory ? mix(feedbackMin, feedbackMax, confidence) : 0.0;
}

float TsrComputeSharpenStrength(
    bool nativeResolution,
    float historyWeight,
    float reactiveMask)
{
    return (nativeResolution ? 0.08 : 0.18)
        * (1.0 - historyWeight)
        * (1.0 - 0.5 * reactiveMask);
}
