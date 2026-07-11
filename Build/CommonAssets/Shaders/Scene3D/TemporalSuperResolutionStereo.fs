#version 460
#extension GL_OVR_multiview2 : require

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray PostProcessOutputTexture;
uniform sampler2DArray Velocity;
uniform sampler2DArray DepthView;
uniform sampler2DArray HistoryDepth;
uniform sampler2DArray TsrHistoryColor;
uniform usampler2DArray StencilView;

uniform bool HistoryReady;
uniform vec2 SourceTexelSize;
uniform vec2 HistoryTexelSize;
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
    vec2 halfTexel = texelSize * 0.5;
    return clamp(uv, halfTexel, vec2(1.0) - halfTexel);
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

void ComputeNeighborhoodBounds(vec2 uv, out vec3 minColor, out vec3 maxColor, out vec3 meanColor)
{
    vec3 m1 = vec3(0.0);
    vec3 m2 = vec3(0.0);
    vec2 sourceTexelSize = TextureTexelSize(PostProcessOutputTexture);

    for (int x = -1; x <= 1; ++x)
    {
        for (int y = -1; y <= 1; ++y)
        {
            vec2 sampleUv = ClampSourceUv(uv + vec2(float(x), float(y)) * sourceTexelSize);
            vec3 s = RGBToYCoCg(texture(PostProcessOutputTexture, EyeUv(sampleUv)).rgb);
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

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x < -1.0 || clipXY.x > 1.0 || clipXY.y < -1.0 || clipXY.y > 1.0)
        discard;

    vec2 uv = ClampSourceUv(
        XRENGINE_FramebufferUV(gl_FragCoord.xy, ScreenOrigin, vec2(ScreenWidth, ScreenHeight)));

    float depthDiscontinuity = EvaluateDepthDiscontinuity(uv);
    vec2 velocity = texture(Velocity, EyeUv(uv)).xy;
    if (depthDiscontinuity > 1e-4)
        velocity = FindClosestVelocity(uv);

    float postTemporalCoverage = SamplePostTemporalForwardMask(uv);
    if (postTemporalCoverage > 0.5)
        velocity = vec2(0.0);

    // Velocity is encoded from unjittered NDC matrices. Convert it to UV and
    // account for projection-sample displacement exactly once here.
    vec2 historyUV = uv - velocity * 0.5 + PreviousJitterUv - CurrentJitterUv;
    vec2 sourceTexelSize = TextureTexelSize(PostProcessOutputTexture);
    vec2 historyTexelSize = TextureTexelSize(TsrHistoryColor);
    bool nativeResolution = all(equal(
        textureSize(PostProcessOutputTexture, 0).xy,
        textureSize(TsrHistoryColor, 0).xy));

    vec4 currentSample = texture(PostProcessOutputTexture, EyeUv(uv));
    vec3 currentColorRaw = currentSample.rgb;
    // Native resolution keeps the exact current sample but still performs the
    // per-eye temporal history resolve below.
    vec3 currentColor = nativeResolution
        ? currentColorRaw
        : mix(currentColorRaw, SampleCurrentReconstruction(PostProcessOutputTexture, uv, sourceTexelSize), 0.25);
    vec3 currentYCoCg = RGBToYCoCg(currentColor);
    float currentLuma = currentYCoCg.x;

    vec3 minBound;
    vec3 maxBound;
    vec3 meanYCoCg;
    ComputeNeighborhoodBounds(uv, minBound, maxBound, meanYCoCg);

    float currentDepth = texture(DepthView, EyeUv(uv)).r;
    vec3 historyYCoCg = currentYCoCg;
    bool canUseHistory = HistoryReady && IsValidUV(historyUV);

    if (canUseHistory)
    {
        vec2 historySampleUv = ClampHistoryUv(historyUV);
        float historyDepth = texture(HistoryDepth, EyeUv(historySampleUv)).r;
        if (abs(historyDepth - currentDepth) <= DepthRejectThreshold)
            historyYCoCg = RGBToYCoCg(SampleCatmullRom(TsrHistoryColor, historySampleUv, historyTexelSize));
        else
            canUseHistory = false;
    }

    vec3 clippedHistory = ClipToAABB(historyYCoCg, minBound, maxBound);
    float motionMask = smoothstep(max(ReactiveVelocityScale * 0.05, 0.0005), ReactiveVelocityScale, length(velocity));
    float geometryInstability = depthDiscontinuity * mix(0.2, 1.0, motionMask);
    float transparencyMask = smoothstep(ReactiveTransparencyRange.x, ReactiveTransparencyRange.y, 1.0 - clamp(currentSample.a, 0.0, 1.0)) * max(0.15, motionMask);
    float luminanceMask = smoothstep(0.25 * ReactiveLumaThreshold, ReactiveLumaThreshold, abs(currentLuma - clippedHistory.x)) * motionMask;
    float reactiveMask = clamp(max(transparencyMask, max(motionMask, luminanceMask)), 0.0, 1.0);
    float confidence = pow(clamp((1.0 - geometryInstability) * (1.0 - reactiveMask), 0.0, 1.0), ConfidencePower);
    float staticConfidenceFloor = (1.0 - motionMask) * (1.0 - reactiveMask) * 0.5;
    confidence = max(confidence, staticConfidenceFloor);
    float historyWeight = canUseHistory ? mix(FeedbackMin, FeedbackMax, confidence) : 0.0;
    if (postTemporalCoverage > 0.5)
        historyWeight = 0.0;

    if (DebugMode != 0)
    {
        vec3 debugColor = DebugMode == 2 ? EncodeVelocityDebug(velocity) : vec3(historyWeight);
        OutColor = vec4(debugColor, 1.0);
        return;
    }

    vec3 result = YCoCgToRGB(mix(currentYCoCg, clippedHistory, historyWeight));
    if (postTemporalCoverage <= 0.0)
    {
        vec3 neighbors =
            texture(PostProcessOutputTexture, EyeUv(ClampSourceUv(uv + vec2(-1.0, 0.0) * sourceTexelSize))).rgb +
            texture(PostProcessOutputTexture, EyeUv(ClampSourceUv(uv + vec2( 1.0, 0.0) * sourceTexelSize))).rgb +
            texture(PostProcessOutputTexture, EyeUv(ClampSourceUv(uv + vec2(0.0, -1.0) * sourceTexelSize))).rgb +
            texture(PostProcessOutputTexture, EyeUv(ClampSourceUv(uv + vec2(0.0,  1.0) * sourceTexelSize))).rgb;
        vec3 highFreq = currentColorRaw - neighbors * 0.25;
        float sharpenStrength = (nativeResolution ? 0.08 : 0.18)
            * (1.0 - historyWeight)
            * (1.0 - 0.5 * reactiveMask);
        result += highFreq * sharpenStrength;
    }

    OutColor = vec4(max(result, vec3(0.0)), 1.0);
}
