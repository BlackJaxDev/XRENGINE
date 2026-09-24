// Advanced TSR surface correspondence. The entry point supplies texture loads
// for either a mono texture or the active array layer.
#ifdef XR_TSR_STABLE_OUTPUT
uniform mat4 TsrCurrentToPreviousClip;
uniform mat4 TsrPreviousInverseProjection;
uniform bool TsrDepthReprojectionReady;
uniform bool TsrClipDepthZeroToOne;
uniform bool TsrReversedDepth;
#ifdef XR_TSR_STEREO
uniform mat4 TsrCurrentToPreviousClipRight;
uniform mat4 TsrPreviousInverseProjectionRight;
uniform bool TsrClipDepthZeroToOneRight;
uniform bool TsrReversedDepthRight;
#define TSR_PREVIOUS_CLIP (gl_ViewID_OVR == 0u ? TsrCurrentToPreviousClip : TsrCurrentToPreviousClipRight)
#define TSR_PREVIOUS_INVERSE_PROJECTION (gl_ViewID_OVR == 0u ? TsrPreviousInverseProjection : TsrPreviousInverseProjectionRight)
#define TSR_ZERO_TO_ONE (gl_ViewID_OVR == 0u ? TsrClipDepthZeroToOne : TsrClipDepthZeroToOneRight)
#define TSR_REVERSED_DEPTH (gl_ViewID_OVR == 0u ? TsrReversedDepth : TsrReversedDepthRight)
#else
#define TSR_PREVIOUS_CLIP TsrCurrentToPreviousClip
#define TSR_PREVIOUS_INVERSE_PROJECTION TsrPreviousInverseProjection
#define TSR_ZERO_TO_ONE TsrClipDepthZeroToOne
#define TSR_REVERSED_DEPTH TsrReversedDepth
#endif

struct TsrSurfaceSample
{
    vec2 uv;
    vec2 velocity;
    float depth;
};

bool TsrIsFarDepth(float depth)
{
    return TSR_REVERSED_DEPTH ? depth <= 1e-7 : depth >= 1.0 - 1e-7;
}

float TsrClipDepth(float depth)
{
    return TSR_ZERO_TO_ONE ? depth : depth * 2.0 - 1.0;
}

TsrSurfaceSample TsrSelectSurface(vec2 sourceUv)
{
    ivec2 extent = textureSize(DepthView, 0).xy;
    vec2 position = sourceUv * vec2(extent) - 0.5;
    ivec2 basePixel = ivec2(floor(position));
    vec2 fraction = fract(position);
    TsrSurfaceSample result;
    result.depth = TSR_REVERSED_DEPTH ? -1.0 : 2.0;
    result.uv = sourceUv;
    result.velocity = vec2(0.0);
    // Dilation is bounded to the current color's bilinear footprint. Depth and
    // motion must describe the same texel, including at foreground silhouettes.
    for (int y = 0; y < 2; ++y)
    for (int x = 0; x < 2; ++x)
    {
        float weight = (x == 0 ? 1.0 - fraction.x : fraction.x)
            * (y == 0 ? 1.0 - fraction.y : fraction.y);
        if (weight <= 1e-5)
            continue;
        ivec2 pixel = clamp(basePixel + ivec2(x, y), ivec2(0), extent - 1);
        float depth = TsrLoadCurrentDepth(pixel);
        if (TSR_REVERSED_DEPTH ? depth > result.depth : depth < result.depth)
        {
            result.depth = depth;
            result.uv = (vec2(pixel) + 0.5) / vec2(extent);
            result.velocity = TsrLoadVelocity(pixel);
        }
    }
    return result;
}

vec3 TsrPreviousViewPosition(vec2 clipXY, float clipZ)
{
    vec4 position = TSR_PREVIOUS_INVERSE_PROJECTION * vec4(clipXY, clipZ, 1.0);
    return position.xyz / position.w;
}

float TsrHistorySurfaceDepth(inout vec2 sourceUv)
{
    ivec2 extent = textureSize(HistoryDepth, 0).xy;
    vec2 position = sourceUv * vec2(extent) - 0.5;
    ivec2 basePixel = ivec2(floor(position));
    vec2 fraction = fract(position);
    float selected = TSR_REVERSED_DEPTH ? -1.0 : 2.0;
    // Previous raw samples use the same bilinear reconstruction footprint.
    // Known mixed-surface accumulation is admitted only by the coverage path.
    for (int y = 0; y < 2; ++y)
    for (int x = 0; x < 2; ++x)
    {
        float weight = (x == 0 ? 1.0 - fraction.x : fraction.x)
            * (y == 0 ? 1.0 - fraction.y : fraction.y);
        if (weight <= 1e-5)
            continue;
        ivec2 pixel = clamp(basePixel + ivec2(x, y), ivec2(0), extent - 1);
        float depth = TsrLoadHistoryDepth(pixel);
        if (TSR_REVERSED_DEPTH ? depth > selected : depth < selected)
        {
            selected = depth;
            sourceUv = (vec2(pixel) + 0.5) / vec2(extent);
        }
    }
    return selected;
}

float TsrDepthGradient(ivec2 pixel, ivec2 axis, float center)
{
    ivec2 extent = textureSize(DepthView, 0).xy;
    float minusDepth = TsrLoadCurrentDepth(clamp(pixel - axis, ivec2(0), extent - 1));
    float plusDepth = TsrLoadCurrentDepth(clamp(pixel + axis, ivec2(0), extent - 1));
    bool minusValid = !TsrIsFarDepth(minusDepth);
    bool plusValid = !TsrIsFarDepth(plusDepth);
    // Prefer the closer neighbor on silhouettes, rather than extending the
    // foreground plane through the background. Raw projected Z is planar.
    float minusDelta = center - minusDepth;
    float plusDelta = plusDepth - center;
    if (!minusValid && !plusValid)
        return 0.0;
    if (!minusValid)
        return plusDelta;
    if (!plusValid)
        return minusDelta;
    // Opposite slopes identify a local extremum, including a one-texel strip
    // between two finite background surfaces. Never fit through that boundary.
    if (minusDelta * plusDelta <= 0.0)
        return 0.0;
    return abs(minusDelta) < abs(plusDelta) ? minusDelta : plusDelta;
}

vec3 TsrPreviousDepthPlane(TsrSurfaceSample surface, vec3 previousNdc)
{
    ivec2 extent = textureSize(DepthView, 0).xy;
    ivec2 pixel = clamp(ivec2(surface.uv * vec2(extent)), ivec2(0), extent - 1);
    vec2 texel = 1.0 / vec2(extent);
    float dx = TsrDepthGradient(pixel, ivec2(1, 0), surface.depth);
    float dy = TsrDepthGradient(pixel, ivec2(0, 1), surface.depth);
    vec4 px = TSR_PREVIOUS_CLIP * vec4(
        XRENGINE_FramebufferTextureUVToClipXY(surface.uv + vec2(texel.x, 0.0)),
        TsrClipDepth(surface.depth + dx), 1.0);
    vec4 py = TSR_PREVIOUS_CLIP * vec4(
        XRENGINE_FramebufferTextureUVToClipXY(surface.uv + vec2(0.0, texel.y)),
        TsrClipDepth(surface.depth + dy), 1.0);
    vec3 normal = cross(px.xyz / px.w - previousNdc, py.xyz / py.w - previousNdc);
    return abs(normal.z) > 1e-12 ? normal / normal.z : vec3(0.0, 0.0, 1.0);
}

bool TsrSampleSurfaceHistory(TsrSurfaceSample surface, vec2 historyUv,
    vec2 currentJitter, vec2 previousJitter, out vec3 color, out float support, out float age,
    out float rejectionReason)
{
    color = vec3(0.0);
    support = 0.0;
    age = 0.0;
    rejectionReason = 1.0;
    if (!TsrDepthReprojectionReady || !TsrIsValidUv(historyUv))
        return false;

    bool background = TsrIsFarDepth(surface.depth);
    float predictedViewDepth = 0.0;
    float depthTolerance = 0.0;
    vec3 predictedNdc = vec3(0.0);
    vec3 depthPlane = vec3(0.0, 0.0, 1.0);
    if (!background)
    {
        vec4 previousClip = TSR_PREVIOUS_CLIP * vec4(
            XRENGINE_FramebufferTextureUVToClipXY(surface.uv), TsrClipDepth(surface.depth), 1.0);
        if (previousClip.w <= 1e-6 || any(isnan(previousClip)) || any(isinf(previousClip)))
        {
            rejectionReason = 2.0;
            return false;
        }
        vec3 previousNdc = previousClip.xyz / previousClip.w;
        predictedNdc = previousNdc;
        depthPlane = TsrPreviousDepthPlane(surface, previousNdc);
        float previousDepth = TSR_ZERO_TO_ONE ? previousNdc.z : previousNdc.z * 0.5 + 0.5;
        vec2 projectedUv = XRENGINE_ClipXYToFramebufferTextureUV(previousNdc.xy);
        vec2 motionUv = surface.uv - currentJitter + previousJitter
            - TsrFramebufferUvDisplacement(surface.velocity * 0.5, FramebufferTextureYDirection);
        // 2D object motion supplies no previous object depth. Only trust the
        // camera depth prediction when its correspondence agrees within a texel.
        vec2 disagreement = abs(projectedUv - motionUv) * vec2(textureSize(HistoryDepth, 0).xy);
        if (!TsrIsValidUv(projectedUv) || previousDepth < 0.0 || previousDepth > 1.0
            || any(greaterThan(disagreement, vec2(0.5))))
        {
            rejectionReason = 3.0;
            return false;
        }
        vec3 previousView = TsrPreviousViewPosition(previousNdc.xy, previousNdc.z);
        predictedViewDepth = abs(previousView.z);
        vec2 depthTexel = 1.0 / vec2(textureSize(HistoryDepth, 0).xy);
        float footprint = max(
            length(TsrPreviousViewPosition(previousNdc.xy + vec2(2.0 * depthTexel.x, 0.0), previousNdc.z) - previousView),
            length(TsrPreviousViewPosition(previousNdc.xy + vec2(0.0, 2.0 * depthTexel.y), previousNdc.z) - previousView));
        // Bound spatial tolerance by one projected pixel; retain a small
        // reconstruction/quantization allowance without comparing raw Z values.
        depthTolerance = min(max(DepthRejectThreshold, 0.0) * predictedViewDepth, footprint)
            + max(1e-5, predictedViewDepth * 2e-5);
    }

    ivec2 historyExtent = textureSize(TsrHistoryColor, 0).xy;
    vec2 position = historyUv * vec2(historyExtent) - 0.5;
    ivec2 basePixel = ivec2(floor(position));
    vec2 fraction = fract(position);
    // Validate each actual bilinear history contributor. A matching neighbor
    // must not admit the wider, incompatible Catmull-Rom color footprint.
    for (int y = 0; y < 2; ++y)
    for (int x = 0; x < 2; ++x)
    {
        float weight = (x == 0 ? 1.0 - fraction.x : fraction.x)
            * (y == 0 ? 1.0 - fraction.y : fraction.y);
        if (weight <= 1e-6)
            continue;
        ivec2 pixel = basePixel + ivec2(x, y);
        if (any(lessThan(pixel, ivec2(0))) || any(greaterThanEqual(pixel, historyExtent)))
            continue;
        vec2 depthUv = (vec2(pixel) + 0.5) / vec2(historyExtent) + previousJitter;
        if (!TsrIsValidUv(depthUv))
            continue;
        float depth = TsrHistorySurfaceDepth(depthUv);
        bool matching = background && TsrIsFarDepth(depth);
        if (!background && !TsrIsFarDepth(depth))
        {
            vec2 sampleClipXY = XRENGINE_FramebufferTextureUVToClipXY(depthUv);
            float expectedClipDepth = predictedNdc.z - dot(depthPlane.xy, sampleClipXY - predictedNdc.xy);
            if (isnan(expectedClipDepth) || isinf(expectedClipDepth)
                || expectedClipDepth < (TSR_ZERO_TO_ONE ? 0.0 : -1.0) || expectedClipDepth > 1.0)
                continue;
            vec3 expectedPosition = TsrPreviousViewPosition(sampleClipXY, expectedClipDepth);
            vec3 observedPosition = TsrPreviousViewPosition(sampleClipXY, TsrClipDepth(depth));
            matching = !any(isnan(expectedPosition)) && !any(isinf(expectedPosition))
                && !any(isnan(observedPosition)) && !any(isinf(observedPosition))
                && abs(abs(observedPosition.z) - abs(expectedPosition.z)) <= depthTolerance;
        }
        if (!matching)
            continue;
        vec4 metadata = TsrLoadHistoryMetadata(pixel);
        if (any(isnan(metadata)) || any(isinf(metadata))
            || (metadata.y > 0.001 && metadata.y < 0.999))
            continue;
        color += TsrLoadHistoryColor(pixel).rgb * weight;
        age += clamp(metadata.x, 0.0, 32.0) * weight;
        support += weight;
    }
    color /= max(support, 1e-6);
    age /= max(support, 1e-6);
    rejectionReason = support >= 0.25 ? 0.0 : 4.0;
    return support >= 0.25;
}
#endif
