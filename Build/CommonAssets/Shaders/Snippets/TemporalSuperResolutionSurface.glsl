// Advanced TSR surface correspondence. The entry point supplies texture loads
// for either a mono texture or the active array layer.
#ifdef XR_TSR_STABLE_OUTPUT
uniform mat4 TsrCurrentToPreviousClip;
uniform mat4 TsrPreviousInverseProjection;
uniform bool TsrDepthReprojectionReady;
uniform bool TsrClipDepthZeroToOne;
uniform bool TsrReversedDepth;
uniform int TsrPortalViewBase;
uniform int TsrPortalReprojectionCount;
uniform uint TsrPortalReprojectionValidMask;
uniform mat4 TsrPortalCurrentToPreviousClip0;
uniform mat4 TsrPortalCurrentToPreviousClip1;
uniform mat4 TsrPortalCurrentToPreviousClip2;
uniform mat4 TsrPortalCurrentToPreviousClip3;
uniform mat4 TsrPortalCurrentToPreviousClip4;
uniform mat4 TsrPortalCurrentToPreviousClip5;
uniform mat4 TsrPortalCurrentToPreviousClip6;
uniform mat4 TsrPortalCurrentToPreviousClip7;
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

// Returns the reflected portal reprojection matrix for pixels owned by an
// in-place portal sub-view whose depth buffer holds reflected geometry depth.
// Primary pixels and scenes with no active portals return the primary matrix.
mat4 TsrPreviousClipForPixel(ivec2 pixel)
{
    if (TsrPortalReprojectionCount > 0)
    {
        uint viewIndex = TsrLoadVisibilityViewIndex(pixel);
        int slot = int(viewIndex) - TsrPortalViewBase;
        if (slot >= 0 && slot < TsrPortalReprojectionCount
            && (TsrPortalReprojectionValidMask & (1u << uint(slot))) != 0u)
        {
            if (slot == 0) return TsrPortalCurrentToPreviousClip0;
            if (slot == 1) return TsrPortalCurrentToPreviousClip1;
            if (slot == 2) return TsrPortalCurrentToPreviousClip2;
            if (slot == 3) return TsrPortalCurrentToPreviousClip3;
            if (slot == 4) return TsrPortalCurrentToPreviousClip4;
            if (slot == 5) return TsrPortalCurrentToPreviousClip5;
            if (slot == 6) return TsrPortalCurrentToPreviousClip6;
            return TsrPortalCurrentToPreviousClip7;
        }
    }
    return TSR_PREVIOUS_CLIP;
}

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

// Evaluates only the view-space depth magnitude from inverse projection without
// computing unused X and Y view coordinates.
float TsrPreviousViewDepth(vec2 clipXY, float clipZ)
{
    vec4 position = TSR_PREVIOUS_INVERSE_PROJECTION * vec4(clipXY, clipZ, 1.0);
    return abs(position.z / position.w);
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

vec3 TsrPreviousDepthPlane(TsrSurfaceSample surface, vec3 previousNdc, mat4 previousClipMatrix)
{
    ivec2 extent = textureSize(DepthView, 0).xy;
    ivec2 pixel = clamp(ivec2(surface.uv * vec2(extent)), ivec2(0), extent - 1);
    vec2 texel = 1.0 / vec2(extent);
    float dx = TsrDepthGradient(pixel, ivec2(1, 0), surface.depth);
    float dy = TsrDepthGradient(pixel, ivec2(0, 1), surface.depth);
    vec4 px = previousClipMatrix * vec4(
        XRENGINE_FramebufferTextureUVToClipXY(surface.uv + vec2(texel.x, 0.0)),
        TsrClipDepth(surface.depth + dx), 1.0);
    vec4 py = previousClipMatrix * vec4(
        XRENGINE_FramebufferTextureUVToClipXY(surface.uv + vec2(0.0, texel.y)),
        TsrClipDepth(surface.depth + dy), 1.0);
    vec3 normal = cross(px.xyz / px.w - previousNdc, py.xyz / py.w - previousNdc);
    return abs(normal.z) > 1e-12 ? normal / normal.z : vec3(0.0, 0.0, 1.0);
}

bool TsrDepthSampleMatchesPlane(vec2 sampleUv, float depth, bool background,
    vec3 predictedNdc, vec3 depthPlane, float depthTolerance)
{
    if (background)
        return TsrIsFarDepth(depth);
    if (TsrIsFarDepth(depth))
        return false;
    vec2 sampleClipXY = XRENGINE_FramebufferTextureUVToClipXY(sampleUv);
    float expectedClipDepth = predictedNdc.z - dot(depthPlane.xy, sampleClipXY - predictedNdc.xy);
    if (isnan(expectedClipDepth) || isinf(expectedClipDepth)
        || expectedClipDepth < (TSR_ZERO_TO_ONE ? 0.0 : -1.0) || expectedClipDepth > 1.0)
        return false;
    float expectedViewDepth = TsrPreviousViewDepth(sampleClipXY, expectedClipDepth);
    float observedViewDepth = TsrPreviousViewDepth(sampleClipXY, TsrClipDepth(depth));
    return !isnan(expectedViewDepth) && !isinf(expectedViewDepth)
        && !isnan(observedViewDepth) && !isinf(observedViewDepth)
        && abs(observedViewDepth - expectedViewDepth) <= depthTolerance;
}

bool TsrHistoryFootprintMatchesPlane(vec2 sourceUv, bool background,
    vec3 predictedNdc, vec3 depthPlane, float depthTolerance, bool stationary)
{
    ivec2 extent = textureSize(HistoryDepth, 0).xy;
    ivec2 center = clamp(ivec2(floor(sourceUv * vec2(extent))), ivec2(0), extent - 1);
    vec2 centerUv = (vec2(center) + 0.5) / vec2(extent);
    float centerDepth = TsrLoadHistoryDepth(center);
    if (TsrDepthSampleMatchesPlane(centerUv, centerDepth, background,
            predictedNdc, depthPlane, depthTolerance))
        return true;

    vec2 nearestUv = sourceUv;
    float nearestDepth = TsrHistorySurfaceDepth(nearestUv);
    if (TsrDepthSampleMatchesPlane(nearestUv, nearestDepth, background,
            predictedNdc, depthPlane, depthTolerance))
        return true;

    // On stationary subpixel edges, projection jitter can shift a silhouette up to
    // one source texel between frames. Validate against the 3x3 previous depth
    // neighborhood only when both camera and local velocity are stationary.
    if (!stationary)
        return false;

    for (int y = -1; y <= 1; ++y)
    for (int x = -1; x <= 1; ++x)
    {
        if (x == 0 && y == 0)
            continue;
        ivec2 pixel = clamp(center + ivec2(x, y), ivec2(0), extent - 1);
        vec2 tapUv = (vec2(pixel) + 0.5) / vec2(extent);
        float tapDepth = TsrLoadHistoryDepth(pixel);
        if (TsrDepthSampleMatchesPlane(tapUv, tapDepth, background,
                predictedNdc, depthPlane, depthTolerance))
            return true;
    }
    return false;
}

bool TsrLocalVelocityStationary(vec2 sourceUv, vec2 extent)
{
    ivec2 velocityExtent = textureSize(Velocity, 0).xy;
    ivec2 center = clamp(ivec2(floor(sourceUv * vec2(velocityExtent))), ivec2(0), velocityExtent - 1);
    if (any(greaterThan(abs(TsrLoadVelocity(center) * 0.5) * extent, vec2(0.1))))
        return false;
    for (int y = -1; y <= 1; ++y)
    for (int x = -1; x <= 1; ++x)
    {
        if (x == 0 && y == 0)
            continue;
        ivec2 pixel = clamp(center + ivec2(x, y), ivec2(0), velocityExtent - 1);
        if (any(greaterThan(abs(TsrLoadVelocity(pixel) * 0.5) * extent, vec2(0.1))))
            return false;
    }
    return true;
}

bool TsrHistoryMotionFootprintMatches(vec2 sourceUv, float predictedViewDepth,
    float motionDepthTolerance)
{
    ivec2 extent = textureSize(HistoryDepth, 0).xy;
    ivec2 center = clamp(ivec2(floor(sourceUv * vec2(extent))), ivec2(0), extent - 1);
    float centerDepth = TsrLoadHistoryDepth(center);
    if (!TsrIsFarDepth(centerDepth))
    {
        vec2 centerUv = (vec2(center) + 0.5) / vec2(extent);
        float centerViewDepth = TsrPreviousViewDepth(
            XRENGINE_FramebufferTextureUVToClipXY(centerUv), TsrClipDepth(centerDepth));
        if (!isnan(centerViewDepth) && !isinf(centerViewDepth)
            && abs(centerViewDepth - predictedViewDepth) <= motionDepthTolerance)
            return true;
    }

    vec2 nearestUv = sourceUv;
    float tapDepth = TsrHistorySurfaceDepth(nearestUv);
    if (!TsrIsFarDepth(tapDepth))
    {
        float tapViewDepth = TsrPreviousViewDepth(
            XRENGINE_FramebufferTextureUVToClipXY(nearestUv), TsrClipDepth(tapDepth));
        if (!isnan(tapViewDepth) && !isinf(tapViewDepth)
            && abs(tapViewDepth - predictedViewDepth) <= motionDepthTolerance)
            return true;
    }

    // Projection jitter can shift a moving silhouette by up to one source texel between frames.
    // Check the 3x3 previous depth neighborhood within the motion depth tolerance so animated
    // avatar silhouettes keep temporal history instead of falling back to raw jittered pixels.
    for (int y = -1; y <= 1; ++y)
    for (int x = -1; x <= 1; ++x)
    {
        if (x == 0 && y == 0)
            continue;
        ivec2 pixel = clamp(center + ivec2(x, y), ivec2(0), extent - 1);
        float neighborDepth = TsrLoadHistoryDepth(pixel);
        if (TsrIsFarDepth(neighborDepth))
            continue;
        vec2 tapUv = (vec2(pixel) + 0.5) / vec2(extent);
        float neighborViewDepth = TsrPreviousViewDepth(
            XRENGINE_FramebufferTextureUVToClipXY(tapUv), TsrClipDepth(neighborDepth));
        if (!isnan(neighborViewDepth) && !isinf(neighborViewDepth)
            && abs(neighborViewDepth - predictedViewDepth) <= motionDepthTolerance)
            return true;
    }
    return false;
}

bool TsrHistoryPixelMatchesSurface(ivec2 pixel, ivec2 historyExtent, vec2 previousJitter,
    bool background, bool objectMotion, bool stationary, vec3 predictedNdc, vec3 depthPlane,
    float depthTolerance, float predictedViewDepth, float motionDepthTolerance)
{
    if (any(lessThan(pixel, ivec2(0))) || any(greaterThanEqual(pixel, historyExtent)))
        return false;
    vec2 depthUv = (vec2(pixel) + 0.5) / vec2(historyExtent) + previousJitter;
    if (!TsrIsValidUv(depthUv))
        return false;
    bool matching = objectMotion
        ? TsrHistoryMotionFootprintMatches(depthUv, predictedViewDepth, motionDepthTolerance)
        : TsrHistoryFootprintMatchesPlane(depthUv, background, predictedNdc, depthPlane,
            depthTolerance, stationary);
    if (!matching)
        return false;
    vec4 metadata = TsrLoadHistoryMetadata(pixel);
    if (any(isnan(metadata)) || any(isinf(metadata))
        || (!objectMotion && metadata.y > 0.001 && metadata.y < 0.999))
        return false;
    return true;
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
    bool stationary = false;
    bool objectMotion = false;
    float predictedViewDepth = 0.0;
    float depthTolerance = 0.0;
    float motionDepthTolerance = 0.0;
    vec3 predictedNdc = vec3(0.0);
    vec3 depthPlane = vec3(0.0, 0.0, 1.0);
    vec2 historyDepthExtent = vec2(textureSize(HistoryDepth, 0).xy);
    if (!background)
    {
        ivec2 depthExtent = textureSize(DepthView, 0).xy;
        ivec2 surfacePixel = clamp(ivec2(surface.uv * vec2(depthExtent)), ivec2(0), depthExtent - 1);
        mat4 previousClipMatrix = TsrPreviousClipForPixel(surfacePixel);
        vec4 previousClip = previousClipMatrix * vec4(
            XRENGINE_FramebufferTextureUVToClipXY(surface.uv), TsrClipDepth(surface.depth), 1.0);
        if (previousClip.w <= 1e-6 || any(isnan(previousClip)) || any(isinf(previousClip)))
        {
            rejectionReason = 2.0;
            return false;
        }
        vec3 previousNdc = previousClip.xyz / previousClip.w;
        predictedNdc = previousNdc;
        depthPlane = TsrPreviousDepthPlane(surface, previousNdc, previousClipMatrix);
        float previousDepth = TSR_ZERO_TO_ONE ? previousNdc.z : previousNdc.z * 0.5 + 0.5;
        vec2 projectedUv = XRENGINE_ClipXYToFramebufferTextureUV(previousNdc.xy);
        vec2 staticUv = surface.uv - currentJitter + previousJitter;
        vec2 motionUv = staticUv
            - TsrFramebufferUvDisplacement(surface.velocity * 0.5, FramebufferTextureYDirection);
        // Compare camera-only reprojection with object-motion reprojection. Match the 0.08-texel
        // threshold to TsrLocalVelocityStationary so subtle avatar breathing and sway enter the
        // motion-validated path instead of falling into a dead zone between static and moving checks.
        vec2 disagreement = abs(projectedUv - motionUv) * historyDepthExtent;
        objectMotion = any(greaterThan(disagreement, vec2(0.08)));
        if (objectMotion)
        {
            if (!TsrIsValidUv(motionUv))
            {
                rejectionReason = 3.0;
                return false;
            }
        }
        else if (!TsrIsValidUv(projectedUv) || previousDepth < 0.0 || previousDepth > 1.0)
        {
            rejectionReason = 3.0;
            return false;
        }
        stationary = !objectMotion
            && all(lessThanEqual(abs(projectedUv - staticUv) * historyDepthExtent, vec2(0.1)))
            && TsrLocalVelocityStationary(surface.uv, historyDepthExtent);
        vec3 previousView = TsrPreviousViewPosition(previousNdc.xy, previousNdc.z);
        predictedViewDepth = abs(previousView.z);
        vec2 depthTexel = 1.0 / historyDepthExtent;
        float footprint = max(
            length(TsrPreviousViewPosition(previousNdc.xy + vec2(2.0 * depthTexel.x, 0.0), previousNdc.z) - previousView),
            length(TsrPreviousViewPosition(previousNdc.xy + vec2(0.0, 2.0 * depthTexel.y), previousNdc.z) - previousView));
        // Bound spatial tolerance by the depth threshold and a small multiple of
        // the projected pixel footprint so grazing/curved ridges stay stable.
        depthTolerance = min(max(DepthRejectThreshold, 0.0) * predictedViewDepth, 2.5 * footprint)
            + max(1e-5, predictedViewDepth * 2e-5);
        motionDepthTolerance = max(max(DepthRejectThreshold, 0.04) * predictedViewDepth, 5.0 * footprint)
            + max(1e-4, predictedViewDepth * 1e-4);
    }
    else
    {
        stationary = TsrLocalVelocityStationary(surface.uv, historyDepthExtent);
    }

    ivec2 historyExtent = textureSize(TsrHistoryColor, 0).xy;
    vec2 position = historyUv * vec2(historyExtent) - 0.5;
    ivec2 basePixel = ivec2(floor(position));
    vec2 fraction = fract(position);
    vec3 innerColors[4];
    vec3 validMin = vec3(1e20);
    vec3 validMax = vec3(-1e20);
    int innerValidCount = 0;
    // Validate each bilinear history contributor against the target surface before blending.
    for (int y = 0; y < 2; ++y)
    for (int x = 0; x < 2; ++x)
    {
        float weight = (x == 0 ? 1.0 - fraction.x : fraction.x)
            * (y == 0 ? 1.0 - fraction.y : fraction.y);
        ivec2 pixel = basePixel + ivec2(x, y);
        bool matches = TsrHistoryPixelMatchesSurface(pixel, historyExtent, previousJitter,
            background, objectMotion, stationary, predictedNdc, depthPlane,
            depthTolerance, predictedViewDepth, motionDepthTolerance);
        if (!matches)
            continue;
        vec3 tapColor = TsrLoadHistoryColor(pixel).rgb;
        innerColors[y * 2 + x] = tapColor;
        validMin = min(validMin, tapColor);
        validMax = max(validMax, tapColor);
        ++innerValidCount;
        if (weight <= 1e-6)
            continue;
        vec4 metadata = TsrLoadHistoryMetadata(pixel);
        color += tapColor * weight;
        age += clamp(metadata.x, 0.0, 32.0) * weight;
        support += weight;
    }
    color /= max(support, 1e-6);
    age /= max(support, 1e-6);

    // Use surface-validated 5-tap Catmull-Rom when all four inner taps belong to the same
    // surface and historyUv is subpixel-shifted. This stops recursive 2x2 bilinear blur from
    // softening animated avatars and panning cameras while rejecting outer taps across depth edges.
    if (innerValidCount == 4 && support >= 0.999 && AntiAliasingFilterMode != 1
        && any(greaterThan(min(fraction, 1.0 - fraction), vec2(0.02))))
    {
        vec2 f = fraction;
        vec2 f2 = f * f;
        vec2 f3 = f2 * f;
        vec2 w0 = -0.5 * f3 + f2 - 0.5 * f;
        vec2 w1 =  1.5 * f3 - 2.5 * f2 + 1.0;
        vec2 w2 = -1.5 * f3 + 2.0 * f2 + 0.5 * f;
        vec2 w3 =  0.5 * f3 - 0.5 * f2;
        vec2 w12 = w1 + w2;
        vec2 u12 = w2 / max(w12, vec2(1e-6));

        vec3 centerColor = mix(
            mix(innerColors[0], innerColors[1], u12.x),
            mix(innerColors[2], innerColors[3], u12.x),
            u12.y);
        float centerWeight = w12.x * w12.y;
        vec3 bicubicSum = centerColor * centerWeight;
        float bicubicWeight = centerWeight;

        // Validate each outer axial pair against the same surface so negative bicubic lobes
        // never pull background color across a silhouette edge.
        if (TsrHistoryPixelMatchesSurface(basePixel + ivec2(-1, 0), historyExtent, previousJitter,
                background, objectMotion, stationary, predictedNdc, depthPlane,
                depthTolerance, predictedViewDepth, motionDepthTolerance)
            && TsrHistoryPixelMatchesSurface(basePixel + ivec2(-1, 1), historyExtent, previousJitter,
                background, objectMotion, stationary, predictedNdc, depthPlane,
                depthTolerance, predictedViewDepth, motionDepthTolerance))
        {
            vec3 leftColor = mix(
                TsrLoadHistoryColor(basePixel + ivec2(-1, 0)).rgb,
                TsrLoadHistoryColor(basePixel + ivec2(-1, 1)).rgb,
                u12.y);
            float lw = w0.x * w12.y;
            bicubicSum += leftColor * lw;
            bicubicWeight += lw;
            validMin = min(validMin, leftColor);
            validMax = max(validMax, leftColor);
        }
        if (TsrHistoryPixelMatchesSurface(basePixel + ivec2(2, 0), historyExtent, previousJitter,
                background, objectMotion, stationary, predictedNdc, depthPlane,
                depthTolerance, predictedViewDepth, motionDepthTolerance)
            && TsrHistoryPixelMatchesSurface(basePixel + ivec2(2, 1), historyExtent, previousJitter,
                background, objectMotion, stationary, predictedNdc, depthPlane,
                depthTolerance, predictedViewDepth, motionDepthTolerance))
        {
            vec3 rightColor = mix(
                TsrLoadHistoryColor(basePixel + ivec2(2, 0)).rgb,
                TsrLoadHistoryColor(basePixel + ivec2(2, 1)).rgb,
                u12.y);
            float rw = w3.x * w12.y;
            bicubicSum += rightColor * rw;
            bicubicWeight += rw;
            validMin = min(validMin, rightColor);
            validMax = max(validMax, rightColor);
        }
        if (TsrHistoryPixelMatchesSurface(basePixel + ivec2(0, -1), historyExtent, previousJitter,
                background, objectMotion, stationary, predictedNdc, depthPlane,
                depthTolerance, predictedViewDepth, motionDepthTolerance)
            && TsrHistoryPixelMatchesSurface(basePixel + ivec2(1, -1), historyExtent, previousJitter,
                background, objectMotion, stationary, predictedNdc, depthPlane,
                depthTolerance, predictedViewDepth, motionDepthTolerance))
        {
            vec3 topColor = mix(
                TsrLoadHistoryColor(basePixel + ivec2(0, -1)).rgb,
                TsrLoadHistoryColor(basePixel + ivec2(1, -1)).rgb,
                u12.x);
            float tw = w12.x * w0.y;
            bicubicSum += topColor * tw;
            bicubicWeight += tw;
            validMin = min(validMin, topColor);
            validMax = max(validMax, topColor);
        }
        if (TsrHistoryPixelMatchesSurface(basePixel + ivec2(0, 2), historyExtent, previousJitter,
                background, objectMotion, stationary, predictedNdc, depthPlane,
                depthTolerance, predictedViewDepth, motionDepthTolerance)
            && TsrHistoryPixelMatchesSurface(basePixel + ivec2(1, 2), historyExtent, previousJitter,
                background, objectMotion, stationary, predictedNdc, depthPlane,
                depthTolerance, predictedViewDepth, motionDepthTolerance))
        {
            vec3 bottomColor = mix(
                TsrLoadHistoryColor(basePixel + ivec2(0, 2)).rgb,
                TsrLoadHistoryColor(basePixel + ivec2(1, 2)).rgb,
                u12.x);
            float bw = w12.x * w3.y;
            bicubicSum += bottomColor * bw;
            bicubicWeight += bw;
            validMin = min(validMin, bottomColor);
            validMax = max(validMax, bottomColor);
        }

        if (bicubicWeight > 1e-4)
            color = clamp(bicubicSum / bicubicWeight, max(validMin, vec3(0.0)), validMax);
    }

    float minSupport = objectMotion ? 0.1 : 0.25;
    rejectionReason = support >= minSupport ? 0.0 : 4.0;
    return support >= minSupport;
}
#endif
