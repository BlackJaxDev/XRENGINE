// Coverage is a mixture of two validated surfaces, never a dilated depth label.
// The entry point supplies eye-specific integer color/depth/motion loads.
#ifdef XR_TSR_STABLE_OUTPUT
struct TsrCoveragePair
{
    TsrSurfaceSample foreground;
    TsrSurfaceSample background;
    float coverage;
    float foregroundLuma;
    float backgroundLuma;
    vec3 colorMin;
    vec3 colorMax;
};

float TsrCompressLuma(float luma)
{
    luma = max(luma, 0.0);
    return luma / (1.0 + luma);
}

float TsrCoverageDepth(ivec2 pixel, bool history)
{
    return history ? TsrLoadHistoryDepth(pixel) : TsrLoadCurrentDepth(pixel);
}

bool TsrBuildCoveragePair(vec2 sourceUv, bool history, out TsrCoveragePair pair)
{
    ivec2 extent = textureSize(DepthView, 0).xy;
    ivec2 center = ivec2(sourceUv * vec2(extent));
    pair.coverage = 0.0;
    pair.foregroundLuma = 0.0;
    pair.backgroundLuma = 0.0;
    pair.colorMin = vec3(1e20);
    pair.colorMax = vec3(-1e20);
    pair.foreground.depth = TSR_REVERSED_DEPTH ? -1.0 : 2.0;
    pair.background.depth = TSR_REVERSED_DEPTH ? 2.0 : -1.0;
    pair.foreground.velocity = vec2(0.0);
    pair.background.velocity = vec2(0.0);
    // Border clamping could duplicate a single witness into a connected line.
    if (any(lessThan(center, ivec2(1))) || any(greaterThanEqual(center, extent - 1)))
        return false;

    for (int y = -1; y <= 1; ++y)
    for (int x = -1; x <= 1; ++x)
    {
        ivec2 pixel = center + ivec2(x, y);
        float depth = TsrCoverageDepth(pixel, history);
        if (isnan(depth) || isinf(depth))
            return false;
        vec2 uv = (vec2(pixel) + 0.5) / vec2(extent);
        if (TSR_REVERSED_DEPTH ? depth > pair.foreground.depth : depth < pair.foreground.depth)
        {
            pair.foreground.depth = depth;
            pair.foreground.uv = uv;
            if (!history)
                pair.foreground.velocity = TsrLoadVelocity(pixel);
        }
        if (TSR_REVERSED_DEPTH ? depth < pair.background.depth : depth > pair.background.depth)
        {
            pair.background.depth = depth;
            pair.background.uv = uv;
            if (!history)
                pair.background.velocity = TsrLoadVelocity(pixel);
        }
    }
    float separation = abs(pair.foreground.depth - pair.background.depth);
    if (TsrIsFarDepth(pair.foreground.depth) || separation < 1e-5)
        return false;

    // This is only a two-cluster detector. Actual temporal matching below uses
    // projected view-space depth and a bounded pixel-footprint tolerance.
    float clusterTolerance = separation * 0.1 + 1e-7;
    int foregroundCount = 0;
    int backgroundCount = 0;
    uint foregroundMask = 0u;
    vec2 foregroundLumaRange = vec2(1.0, 0.0);
    vec2 backgroundLumaRange = vec2(1.0, 0.0);
    for (int y = -1; y <= 1; ++y)
    for (int x = -1; x <= 1; ++x)
    {
        ivec2 pixel = center + ivec2(x, y);
        float depth = TsrCoverageDepth(pixel, history);
        bool foreground = abs(depth - pair.foreground.depth) <= clusterTolerance;
        bool background = abs(depth - pair.background.depth) <= clusterTolerance;
        // Three layers, a continuous slope, or ambiguous membership cannot lock.
        if (foreground == background)
            return false;
        if (foreground)
        {
            ++foregroundCount;
            foregroundMask |= 1u << uint((y + 1) * 3 + x + 1);
        }
        else
            ++backgroundCount;
        if (!history)
        {
            if (TsrLoadCurrentReactivity(pixel) >= 0.1)
                return false;
            vec3 color = TsrRgbToYCoCg(TsrLoadCurrentColor(pixel));
            pair.colorMin = min(pair.colorMin, color);
            pair.colorMax = max(pair.colorMax, color);
            float luma = TsrCompressLuma(color.x);
            if (foreground)
            {
                pair.foregroundLuma += color.x;
                foregroundLumaRange = vec2(min(foregroundLumaRange.x, luma), max(foregroundLumaRange.y, luma));
            }
            else
            {
                pair.backgroundLuma += color.x;
                backgroundLumaRange = vec2(min(backgroundLumaRange.x, luma), max(backgroundLumaRange.y, luma));
            }
        }
    }
    if (foregroundCount < 2 || backgroundCount < 2)
        return false;
    // Horizontal, vertical and diagonal adjacency all support a connected line.
    bool connected = false;
    for (int y = 0; y < 3; ++y)
    for (int x = 0; x < 3; ++x)
    {
        uint bit = 1u << uint(y * 3 + x);
        if ((foregroundMask & bit) == 0u)
            continue;
        if (x < 2 && (foregroundMask & (bit << 1u)) != 0u)
            connected = true;
        if (y < 2 && (foregroundMask & (bit << 3u)) != 0u)
            connected = true;
        if (y < 2 && x < 2 && (foregroundMask & (bit << 4u)) != 0u)
            connected = true;
        if (y < 2 && x > 0 && (foregroundMask & (bit << 2u)) != 0u)
            connected = true;
    }
    if (!connected)
        return false;
    if (!history)
    {
        pair.foregroundLuma /= float(foregroundCount);
        pair.backgroundLuma /= float(backgroundCount);
        float contrast = abs(TsrCompressLuma(pair.foregroundLuma) - TsrCompressLuma(pair.backgroundLuma));
        if (contrast < 0.04 || max(foregroundLumaRange.y - foregroundLumaRange.x,
                backgroundLumaRange.y - backgroundLumaRange.x) > contrast * 0.35 + 0.02)
            return false;
    }

    vec2 position = sourceUv * vec2(extent) - 0.5;
    ivec2 basePixel = ivec2(floor(position));
    vec2 fraction = fract(position);
    for (int y = 0; y < 2; ++y)
    for (int x = 0; x < 2; ++x)
    {
        float weight = (x == 0 ? 1.0 - fraction.x : fraction.x)
            * (y == 0 ? 1.0 - fraction.y : fraction.y);
        if (weight <= 1e-6)
            continue;
        float depth = TsrCoverageDepth(basePixel + ivec2(x, y), history);
        if (abs(depth - pair.foreground.depth) <= clusterTolerance)
            pair.coverage += weight;
        else if (abs(depth - pair.background.depth) > clusterTolerance)
            return false;
    }
    return true;
}

bool TsrProjectCoverageSurface(TsrSurfaceSample surface, vec2 sourceUv,
    vec2 currentJitter, vec2 previousJitter, out vec2 stableHistoryUv, out float tolerance)
{
    vec4 clip = TSR_PREVIOUS_CLIP * vec4(
        XRENGINE_FramebufferTextureUVToClipXY(surface.uv), TsrClipDepth(surface.depth), 1.0);
    stableHistoryUv = vec2(0.0);
    tolerance = 0.0;
    if (clip.w <= 1e-6 || any(isnan(clip)) || any(isinf(clip)))
        return false;
    vec3 previousNdc = clip.xyz / clip.w;
    bool background = TsrIsFarDepth(surface.depth);
    float sourceDepth = surface.depth;
    if (!background)
    {
        ivec2 extent = textureSize(DepthView, 0).xy;
        ivec2 pixel = ivec2(surface.uv * vec2(extent));
        vec2 offset = (sourceUv - surface.uv) * vec2(extent);
        sourceDepth += offset.x * TsrDepthGradient(pixel, ivec2(1, 0), surface.depth)
            + offset.y * TsrDepthGradient(pixel, ivec2(0, 1), surface.depth);
        vec3 view = TsrPreviousViewPosition(previousNdc.xy, previousNdc.z);
        vec2 texel = 1.0 / vec2(extent);
        float footprint = max(
            length(TsrPreviousViewPosition(previousNdc.xy + vec2(2.0 * texel.x, 0.0), previousNdc.z) - view),
            length(TsrPreviousViewPosition(previousNdc.xy + vec2(0.0, 2.0 * texel.y), previousNdc.z) - view));
        tolerance = min(max(DepthRejectThreshold, 0.0) * abs(view.z), footprint)
            + max(1e-5, abs(view.z) * 2e-5);
    }
    if (sourceDepth < 0.0 || sourceDepth > 1.0)
        return false;
    vec4 ray = TSR_PREVIOUS_CLIP * vec4(
        XRENGINE_FramebufferTextureUVToClipXY(sourceUv), TsrClipDepth(sourceDepth), 1.0);
    if (ray.w <= 1e-6 || any(isnan(ray)) || any(isinf(ray)))
        return false;
    stableHistoryUv = XRENGINE_ClipXYToFramebufferTextureUV(ray.xy / ray.w) - previousJitter;
    vec2 motionUv = sourceUv - currentJitter
        - TsrFramebufferUvDisplacement(surface.velocity * 0.5, FramebufferTextureYDirection);
    return TsrIsValidUv(stableHistoryUv)
        && all(lessThanEqual(abs(stableHistoryUv - motionUv) * vec2(textureSize(DepthView, 0).xy), vec2(0.5)));
}

bool TsrCoverageCameraStable(vec2 currentJitter, vec2 previousJitter)
{
    // A stationary camera leaves only the known projection-jitter translation.
    // Do not approximate infinite sky with a finite far plane during camera motion.
    mat4 expected = mat4(1.0);
    expected[3].xy = 2.0 * TsrFramebufferUvDisplacement(previousJitter - currentJitter,
        FramebufferTextureYDirection);
    for (int column = 0; column < 4; ++column)
        if (any(greaterThan(abs(TSR_PREVIOUS_CLIP[column] - expected[column]), vec4(1e-5))))
            return false;
    return true;
}

bool TsrBuildProjectedCoverageWitnesses(TsrCoveragePair pair, vec2 sourceUv,
    vec2 currentJitter, vec2 previousJitter, out vec4 witnesses[9])
{
    ivec2 extent = textureSize(DepthView, 0).xy;
    ivec2 center = ivec2(sourceUv * vec2(extent));
    float clusterTolerance = abs(pair.foreground.depth - pair.background.depth) * 0.1 + 1e-7;
    for (int y = -1; y <= 1; ++y)
    for (int x = -1; x <= 1; ++x)
    {
        ivec2 pixel = center + ivec2(x, y);
        float depth = TsrLoadCurrentDepth(pixel);
        vec2 uv = (vec2(pixel) + 0.5) / vec2(extent);
        bool foreground = abs(depth - pair.foreground.depth) <= clusterTolerance;
        vec2 projectedUv = uv - currentJitter + previousJitter;
        float viewDepth = -1.0;
        if (!TsrIsFarDepth(depth))
        {
            vec4 previous = TSR_PREVIOUS_CLIP * vec4(
                XRENGINE_FramebufferTextureUVToClipXY(uv), TsrClipDepth(depth), 1.0);
            if (previous.w <= 1e-6 || any(isnan(previous)) || any(isinf(previous)))
                return false;
            vec3 ndc = previous.xyz / previous.w;
            projectedUv = XRENGINE_ClipXYToFramebufferTextureUV(ndc.xy);
            vec2 motionUv = uv - currentJitter + previousJitter
                - TsrFramebufferUvDisplacement(TsrLoadVelocity(pixel) * 0.5, FramebufferTextureYDirection);
            if (any(greaterThan(abs(projectedUv - motionUv) * vec2(extent), vec2(0.5))))
                return false;
            viewDepth = abs(TsrPreviousViewPosition(ndc.xy, ndc.z).z);
            if (isnan(viewDepth) || isinf(viewDepth))
                return false;
        }
        witnesses[(y + 1) * 3 + x + 1] = vec4(projectedUv * vec2(extent), viewDepth, foreground ? 1.0 : 0.0);
    }
    return true;
}

bool TsrValidatePreviousCoverageWitnesses(TsrCoveragePair previousPair, vec2 sourceUv, vec4 witnesses[9],
    float foregroundTolerance, float backgroundTolerance, out float coverage)
{
    ivec2 extent = textureSize(DepthView, 0).xy;
    ivec2 center = ivec2(sourceUv * vec2(extent));
    vec2 position = sourceUv * vec2(extent) - 0.5;
    ivec2 basePixel = ivec2(floor(position));
    vec2 fraction = fract(position);
    int foregroundCount = 0;
    int backgroundCount = 0;
    float clusterTolerance = abs(previousPair.foreground.depth - previousPair.background.depth) * 0.1 + 1e-7;
    coverage = 0.0;
    for (int y = -1; y <= 1; ++y)
    for (int x = -1; x <= 1; ++x)
    {
        ivec2 pixel = center + ivec2(x, y);
        float depth = TsrLoadHistoryDepth(pixel);
        vec2 uv = (vec2(pixel) + 0.5) / vec2(extent);
        bool farDepth = TsrIsFarDepth(depth);
        float viewDepth = farDepth ? -1.0
            : abs(TsrPreviousViewPosition(XRENGINE_FramebufferTextureUVToClipXY(uv), TsrClipDepth(depth)).z);
        if (isnan(viewDepth) || isinf(viewDepth))
            return false;
        bool foreground = false;
        bool background = false;
        for (int i = 0; i < 9; ++i)
        {
            vec4 witness = witnesses[i];
            if (any(greaterThan(abs(witness.xy - (vec2(pixel) + 0.5)), vec2(1.5))))
                continue;
            bool isForeground = witness.w > 0.5;
            float tolerance = isForeground ? foregroundTolerance : backgroundTolerance;
            bool matches = farDepth ? witness.z < 0.0
                : witness.z >= 0.0 && abs(viewDepth - witness.z) <= tolerance;
            foreground = foreground || (matches && isForeground);
            background = background || (matches && !isForeground);
        }
        // Require nearby evidence at the observed depth. A cluster's front and
        // side faces need not share a plane, and gaps between them are not filled.
        ivec2 offset = pixel - basePixel;
        bool colorContributor = all(greaterThanEqual(offset, ivec2(0)))
            && all(lessThanEqual(offset, ivec2(1)));
        float weight = colorContributor
            ? (offset.x == 0 ? 1.0 - fraction.x : fraction.x)
                * (offset.y == 0 ? 1.0 - fraction.y : fraction.y) : 0.0;
        bool previousForeground = abs(depth - previousPair.foreground.depth) <= clusterTolerance;
        if (foreground == background || foreground != previousForeground)
        {
            // Supporting samples do not expand the color footprint. Only
            // actual contributors must all validate; others provide evidence.
            if (weight > 1e-6)
                return false;
            continue;
        }
        if (foreground)
            ++foregroundCount;
        else
            ++backgroundCount;
        if (foreground)
            coverage += weight;
    }
    return foregroundCount >= 2 && backgroundCount >= 2;
}

bool TsrSampleCoverageHistory(TsrCoveragePair current, vec2 sourceUv, vec2 historyUv,
    vec2 currentJitter, vec2 previousJitter, out vec3 color, out vec4 metadata,
    out float previousCoverage, out float failureReason)
{
    failureReason = 4.0;
    color = vec3(0.0);
    metadata = vec4(0.0);
    previousCoverage = 0.0;
    if (TsrIsFarDepth(current.background.depth) && !TsrCoverageCameraStable(currentJitter, previousJitter))
        return false;
    vec2 foregroundUv, backgroundUv;
    float foregroundTolerance, backgroundTolerance;
    if (!TsrProjectCoverageSurface(current.foreground, sourceUv, currentJitter, previousJitter,
            foregroundUv, foregroundTolerance)
        || !TsrProjectCoverageSurface(current.background, sourceUv, currentJitter, previousJitter,
            backgroundUv, backgroundTolerance))
        return false;
    vec2 extent = vec2(textureSize(DepthView, 0).xy);
    // A single history color cannot follow two surfaces with different parallax.
    if (any(greaterThan(abs(foregroundUv - backgroundUv) * extent, vec2(0.25)))
        || any(greaterThan(abs(foregroundUv - historyUv) * extent, vec2(0.25))))
        return false;
    float validatedCoverage;
    vec4 witnesses[9];
    failureReason = 5.0;
    if (!TsrBuildProjectedCoverageWitnesses(current, sourceUv, currentJitter, previousJitter, witnesses))
        return false;

    ivec2 historyExtent = textureSize(TsrHistoryColor, 0).xy;
    vec2 position = historyUv * vec2(historyExtent) - 0.5;
    ivec2 basePixel = ivec2(floor(position));
    vec2 fraction = fract(position);
    float support = 0.0;
    float dominantWeight = 0.0;
    float meanCoverage = 0.0;
    float meanAge = 0.0;
    failureReason = 6.0;
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
        vec4 state = TsrLoadHistoryMetadata(pixel);
        if (any(isnan(state)) || any(isinf(state)) || state.x < 1.0 || state.y < 0.0)
            continue;
        vec2 depthUv = (vec2(pixel) + 0.5) / vec2(historyExtent) + previousJitter;
        TsrCoveragePair previous;
        if (!TsrBuildCoveragePair(depthUv, true, previous))
            continue;
        if (!TsrValidatePreviousCoverageWitnesses(previous, depthUv, witnesses,
                foregroundTolerance, backgroundTolerance, validatedCoverage))
            continue;
        color += TsrLoadHistoryColor(pixel).rgb * weight;
        meanCoverage += clamp(state.y, 0.0, 1.0) * weight;
        meanAge += clamp(state.x, 1.0, 32.0) * weight;
        support += weight;
        // Signed oscillation state and its previous raw luminance must come
        // from one contributor; interpolating opposite signs destroys evidence.
        if (weight > dominantWeight)
        {
            metadata = state;
            previousCoverage = validatedCoverage;
            dominantWeight = weight;
        }
    }
    color /= max(support, 1e-6);
    metadata.x = meanAge / max(support, 1e-6);
    metadata.y = meanCoverage / max(support, 1e-6);
    if (support < 0.75)
        return false;
    failureReason = 0.0;
    return true;
}

float TsrUpdateFlicker(TsrCoveragePair pair, vec4 history, float previousCoverage, float currentLuma,
    out bool unexplainedChange)
{
    unexplainedChange = false;
    float delta = TsrCompressLuma(currentLuma) - history.z;
    float previousMix = mix(pair.backgroundLuma, pair.foregroundLuma, previousCoverage);
    float currentMix = mix(pair.backgroundLuma, pair.foregroundLuma, pair.coverage);
    float expectedDelta = TsrCompressLuma(currentMix) - TsrCompressLuma(previousMix);
    float confidence = abs(history.w);
    if (abs(delta) < 0.002)
        return sign(history.w) * confidence * 0.98;
    // Locks grow only for reversals explained by changes of geometric coverage.
    // Lighting trends or animated shading must not create a persistent lock.
    bool coverageExplains = delta * expectedDelta > 0.0
        && abs(delta - expectedDelta) <= 0.02 + abs(expectedDelta) * 0.35;
    if (!coverageExplains)
    {
        unexplainedChange = true;
        return 0.0;
    }
    bool reversal = delta * history.w < 0.0;
    confidence = reversal ? min(1.0, confidence + 0.25) : max(0.05, confidence * 0.9);
    return sign(delta) * confidence;
}

struct TsrCoverageResult
{
    bool historyValid;
    float failureReason;
    float currentCoverage;
    float historyCoverage;
    float historyAge;
    float flicker;
    float retention;
    vec3 historyColor;
    vec3 colorMin;
    vec3 colorMax;
};

TsrCoverageResult TsrResolveCoverage(vec2 sourceUv, vec2 historyUv,
    vec2 currentJitter, vec2 previousJitter, bool historyAllowed,
    float reactiveMask, float motionMask, float currentLuma, float depthDiscontinuity)
{
    TsrCoverageResult result;
    result.historyValid = false;
    result.failureReason = 1.0;
    result.currentCoverage = -1.0;
    result.historyCoverage = -1.0;
    result.historyAge = 1.0;
    result.flicker = 0.0;
    result.retention = 0.0;
    result.historyColor = vec3(0.0);
    result.colorMin = vec3(0.0);
    result.colorMax = vec3(0.0);
    if (!TsrDepthReprojectionReady || reactiveMask >= 0.1 || motionMask >= 0.1
        || depthDiscontinuity <= 1e-4)
        return result;
    TsrCoveragePair current;
    result.failureReason = 2.0;
    if (!TsrBuildCoveragePair(sourceUv, false, current))
        return result;
    result.currentCoverage = current.coverage;
    result.colorMin = current.colorMin;
    result.colorMax = current.colorMax;
    result.failureReason = 3.0;
    if (!historyAllowed)
        return result;
    vec4 metadata;
    float previousCoverage;
    result.historyValid = TsrSampleCoverageHistory(current, sourceUv, historyUv,
        currentJitter, previousJitter, result.historyColor, metadata, previousCoverage, result.failureReason);
    if (!result.historyValid)
    {
        if (result.failureReason < 6.0)
            result.currentCoverage = -1.0;
        return result;
    }
    result.historyCoverage = metadata.y;
    result.historyAge = metadata.x;
    bool unexplainedChange;
    result.flicker = TsrUpdateFlicker(current, metadata, previousCoverage, currentLuma, unexplainedChange);
    if (unexplainedChange)
    {
        result.historyValid = false;
        result.failureReason = 7.0;
        result.historyCoverage = -1.0;
        result.historyAge = 1.0;
        return result;
    }
    float partialCoverage = smoothstep(0.0, 0.1, min(metadata.y, 1.0 - metadata.y));
    result.retention = smoothstep(0.15, 0.6, abs(result.flicker)) * partialCoverage
        * (1.0 - motionMask) * (1.0 - reactiveMask);
    return result;
}
#endif
