// Shadow moment encoding and sampling helpers.

#ifndef XRENGINE_SHADOW_MOMENT_ENCODING_GLSL
#define XRENGINE_SHADOW_MOMENT_ENCODING_GLSL

#define XRENGINE_SHADOW_ENCODING_DEPTH 0
#define XRENGINE_SHADOW_ENCODING_VARIANCE2 1
#define XRENGINE_SHADOW_ENCODING_EVSM2 2
#define XRENGINE_SHADOW_ENCODING_EVSM4 3

#ifndef XRENGINE_SHADOW_MOMENT_UNIFORMS
#define XRENGINE_SHADOW_MOMENT_UNIFORMS
uniform int ShadowMapEncoding = XRENGINE_SHADOW_ENCODING_DEPTH;
uniform float ShadowMomentMinVariance = 0.00002;
uniform float ShadowMomentLightBleedReduction = 0.2;
uniform float ShadowMomentPositiveExponent = 5.0;
uniform float ShadowMomentNegativeExponent = 5.0;
uniform float ShadowMomentMipBias = 0.0;
uniform int ShadowDepthSourceMode = 0; // 0 = perspective camera depth, 1 = projected depth already normalized.
#endif

#ifndef XRENGINE_CAMERA_DEPTH_RANGE_UNIFORMS
#define XRENGINE_CAMERA_DEPTH_RANGE_UNIFORMS
uniform float CameraNearZ = 0.1;
uniform float CameraFarZ = 1000.0;
#endif

float XRENGINE_LinearizeShadowDepth01(float depth, float nearZ, float farZ)
{
    float n = max(nearZ, 0.0001);
    float f = max(farZ, n + 0.0001);
    float z = depth * 2.0 - 1.0;
    float linearZ = (2.0 * n * f) / (f + n - z * (f - n));
    return clamp((linearZ - n) / (f - n), 0.0, 1.0);
}

vec2 XRENGINE_EncodeVsmMoments(float depth, bool includeDerivativeVariance)
{
    float clampedDepth = clamp(depth, 0.0, 1.0);
    float moment2 = clampedDepth * clampedDepth;
    if (includeDerivativeVariance)
    {
        float dx = dFdx(clampedDepth);
        float dy = dFdy(clampedDepth);
        moment2 += 0.25 * (dx * dx + dy * dy);
    }

    return vec2(clampedDepth, moment2);
}

vec2 XRENGINE_EncodeEvsm2Moments(float depth, float positiveExponent, bool includeDerivativeVariance)
{
    float warpedDepth = exp(max(positiveExponent, 0.0) * clamp(depth, 0.0, 1.0));
    float moment2 = warpedDepth * warpedDepth;
    if (includeDerivativeVariance)
    {
        float dx = dFdx(warpedDepth);
        float dy = dFdy(warpedDepth);
        moment2 += 0.25 * (dx * dx + dy * dy);
    }

    return vec2(warpedDepth, moment2);
}

vec4 XRENGINE_EncodeEvsm4Moments(float depth, float positiveExponent, float negativeExponent, bool includeDerivativeVariance)
{
    float clampedDepth = clamp(depth, 0.0, 1.0);
    float positive = exp(max(positiveExponent, 0.0) * clampedDepth);
    float negative = -exp(-max(negativeExponent, 0.0) * clampedDepth);
    vec4 moments = vec4(positive, positive * positive, negative, negative * negative);

    if (includeDerivativeVariance)
    {
        float positiveDx = dFdx(positive);
        float positiveDy = dFdy(positive);
        float negativeDx = dFdx(negative);
        float negativeDy = dFdy(negative);
        moments.y += 0.25 * (positiveDx * positiveDx + positiveDy * positiveDy);
        moments.w += 0.25 * (negativeDx * negativeDx + negativeDy * negativeDy);
    }

    return moments;
}

vec4 XRENGINE_EncodeShadowMoments(
    int encoding,
    float normalizedDepth,
    float positiveExponent,
    float negativeExponent,
    bool includeDerivativeVariance)
{
    if (encoding == XRENGINE_SHADOW_ENCODING_VARIANCE2)
        return vec4(XRENGINE_EncodeVsmMoments(normalizedDepth, includeDerivativeVariance), 0.0, 0.0);
    if (encoding == XRENGINE_SHADOW_ENCODING_EVSM2)
        return vec4(XRENGINE_EncodeEvsm2Moments(normalizedDepth, positiveExponent, includeDerivativeVariance), 0.0, 0.0);
    if (encoding == XRENGINE_SHADOW_ENCODING_EVSM4)
        return XRENGINE_EncodeEvsm4Moments(normalizedDepth, positiveExponent, negativeExponent, includeDerivativeVariance);

    return vec4(clamp(normalizedDepth, 0.0, 1.0), 0.0, 0.0, 0.0);
}

void XRENGINE_WriteShadowCasterDepth(out vec4 outputDepth, float projectedDepth)
{
    if (ShadowMapEncoding == XRENGINE_SHADOW_ENCODING_DEPTH)
    {
        outputDepth = vec4(projectedDepth, 0.0, 0.0, 0.0);
        return;
    }

    float normalizedDepth = ShadowDepthSourceMode == 1
        ? clamp(projectedDepth, 0.0, 1.0)
        : XRENGINE_LinearizeShadowDepth01(projectedDepth, CameraNearZ, CameraFarZ);
    outputDepth = XRENGINE_EncodeShadowMoments(
        ShadowMapEncoding,
        normalizedDepth,
        ShadowMomentPositiveExponent,
        ShadowMomentNegativeExponent,
        false);
}

void XRENGINE_WritePointShadowCasterDepth(out vec4 outputDepth, vec3 fragPos, vec3 lightPos, float farPlaneDist)
{
    float normalizedDepth = clamp(length(fragPos - lightPos) / max(farPlaneDist, 0.0001), 0.0, 1.0);
    outputDepth = XRENGINE_EncodeShadowMoments(
        ShadowMapEncoding,
        normalizedDepth,
        ShadowMomentPositiveExponent,
        ShadowMomentNegativeExponent,
        false);
}

float XRENGINE_ReduceLightBleeding(float probability, float lightBleedReduction)
{
    float amount = clamp(lightBleedReduction, 0.0, 0.999);
    return clamp((probability - amount) / max(1.0 - amount, 0.0001), 0.0, 1.0);
}

float XRENGINE_ChebyshevUpperBound(vec2 moments, float receiverDepth, float minVariance, float lightBleedReduction)
{
    if (receiverDepth <= moments.x)
        return 1.0;

    float variance = max(moments.y - moments.x * moments.x, minVariance);
    float depthDelta = receiverDepth - moments.x;
    float probability = variance / (variance + depthDelta * depthDelta);
    return XRENGINE_ReduceLightBleeding(probability, lightBleedReduction);
}

float XRENGINE_ResolveShadowMomentMipLevel(float mipLevel, float blurRadiusTexels, bool useMipmaps)
{
    if (!useMipmaps)
        return 0.0;

    float radiusLod = blurRadiusTexels > 1.0
        ? log2(blurRadiusTexels)
        : 0.0;
    return max(mipLevel + radiusLod, 0.0);
}

vec4 XRENGINE_FetchShadowMoments2D(
    sampler2D shadowMap,
    vec2 uv,
    float mipLevel,
    bool useMipmaps)
{
    return useMipmaps
        ? textureLod(shadowMap, uv, max(mipLevel, 0.0))
        : texture(shadowMap, uv);
}

vec4 XRENGINE_FetchShadowMomentsCube(
    samplerCube shadowMap,
    vec3 direction,
    float mipLevel,
    bool useMipmaps)
{
    vec3 sampleDirection = normalize(direction);
    return useMipmaps
        ? textureLod(shadowMap, sampleDirection, max(mipLevel, 0.0))
        : texture(shadowMap, sampleDirection);
}

vec4 XRENGINE_FetchShadowMoments2DArray(
    sampler2DArray shadowMap,
    vec2 uv,
    float layer,
    float mipLevel,
    bool useMipmaps)
{
    vec3 sampleCoord = vec3(uv, layer);
    return useMipmaps
        ? textureLod(shadowMap, sampleCoord, max(mipLevel, 0.0))
        : texture(shadowMap, sampleCoord);
}

float XRENGINE_SampleShadowMoment2D(
    sampler2D shadowMap,
    vec2 uv,
    float receiverDepth,
    int encoding,
    float minVariance,
    float lightBleedReduction,
    float positiveExponent,
    float negativeExponent,
    float mipLevel,
    bool useMipmaps)
{
    vec4 moments = XRENGINE_FetchShadowMoments2D(shadowMap, uv, mipLevel, useMipmaps);
    float clampedReceiver = clamp(receiverDepth, 0.0, 1.0);

    if (encoding == XRENGINE_SHADOW_ENCODING_VARIANCE2)
    {
        return XRENGINE_ChebyshevUpperBound(moments.xy, clampedReceiver, minVariance, lightBleedReduction);
    }

    if (encoding == XRENGINE_SHADOW_ENCODING_EVSM2)
    {
        float warpedReceiver = exp(max(positiveExponent, 0.0) * clampedReceiver);
        return XRENGINE_ChebyshevUpperBound(moments.xy, warpedReceiver, minVariance, lightBleedReduction);
    }

    if (encoding == XRENGINE_SHADOW_ENCODING_EVSM4)
    {
        float warpedPositive = exp(max(positiveExponent, 0.0) * clampedReceiver);
        float warpedNegative = -exp(-max(negativeExponent, 0.0) * clampedReceiver);
        float positiveVisibility = XRENGINE_ChebyshevUpperBound(moments.xy, warpedPositive, minVariance, lightBleedReduction);
        float negativeVisibility = XRENGINE_ChebyshevUpperBound(moments.zw, warpedNegative, minVariance, lightBleedReduction);
        return min(positiveVisibility, negativeVisibility);
    }

    return (clampedReceiver <= moments.r) ? 1.0 : 0.0;
}

float XRENGINE_SampleShadowMoment2DArray(
    sampler2DArray shadowMap,
    vec2 uv,
    float layer,
    float receiverDepth,
    int encoding,
    float minVariance,
    float lightBleedReduction,
    float positiveExponent,
    float negativeExponent,
    float mipLevel,
    bool useMipmaps)
{
    vec4 moments = XRENGINE_FetchShadowMoments2DArray(shadowMap, uv, layer, mipLevel, useMipmaps);
    float clampedReceiver = clamp(receiverDepth, 0.0, 1.0);

    if (encoding == XRENGINE_SHADOW_ENCODING_VARIANCE2)
    {
        return XRENGINE_ChebyshevUpperBound(moments.xy, clampedReceiver, minVariance, lightBleedReduction);
    }

    if (encoding == XRENGINE_SHADOW_ENCODING_EVSM2)
    {
        float warpedReceiver = exp(max(positiveExponent, 0.0) * clampedReceiver);
        return XRENGINE_ChebyshevUpperBound(moments.xy, warpedReceiver, minVariance, lightBleedReduction);
    }

    if (encoding == XRENGINE_SHADOW_ENCODING_EVSM4)
    {
        float warpedPositive = exp(max(positiveExponent, 0.0) * clampedReceiver);
        float warpedNegative = -exp(-max(negativeExponent, 0.0) * clampedReceiver);
        float positiveVisibility = XRENGINE_ChebyshevUpperBound(moments.xy, warpedPositive, minVariance, lightBleedReduction);
        float negativeVisibility = XRENGINE_ChebyshevUpperBound(moments.zw, warpedNegative, minVariance, lightBleedReduction);
        return min(positiveVisibility, negativeVisibility);
    }

    return (clampedReceiver <= moments.r) ? 1.0 : 0.0;
}

float XRENGINE_SampleShadowMomentCube(
    samplerCube shadowMap,
    vec3 direction,
    float receiverDepth,
    int encoding,
    float minVariance,
    float lightBleedReduction,
    float positiveExponent,
    float negativeExponent,
    float mipLevel,
    bool useMipmaps)
{
    vec4 moments = XRENGINE_FetchShadowMomentsCube(shadowMap, direction, mipLevel, useMipmaps);
    float clampedReceiver = clamp(receiverDepth, 0.0, 1.0);

    if (encoding == XRENGINE_SHADOW_ENCODING_VARIANCE2)
    {
        return XRENGINE_ChebyshevUpperBound(moments.xy, clampedReceiver, minVariance, lightBleedReduction);
    }

    if (encoding == XRENGINE_SHADOW_ENCODING_EVSM2)
    {
        float warpedReceiver = exp(max(positiveExponent, 0.0) * clampedReceiver);
        return XRENGINE_ChebyshevUpperBound(moments.xy, warpedReceiver, minVariance, lightBleedReduction);
    }

    if (encoding == XRENGINE_SHADOW_ENCODING_EVSM4)
    {
        float warpedPositive = exp(max(positiveExponent, 0.0) * clampedReceiver);
        float warpedNegative = -exp(-max(negativeExponent, 0.0) * clampedReceiver);
        float positiveVisibility = XRENGINE_ChebyshevUpperBound(moments.xy, warpedPositive, minVariance, lightBleedReduction);
        float negativeVisibility = XRENGINE_ChebyshevUpperBound(moments.zw, warpedNegative, minVariance, lightBleedReduction);
        return min(positiveVisibility, negativeVisibility);
    }

    return (clampedReceiver <= moments.r) ? 1.0 : 0.0;
}

float XRENGINE_SampleShadowMoment2D(
    sampler2D shadowMap,
    vec2 uv,
    float receiverDepth,
    int encoding,
    float minVariance,
    float lightBleedReduction,
    float positiveExponent,
    float negativeExponent,
    float mipLevel)
{
    return XRENGINE_SampleShadowMoment2D(
        shadowMap,
        uv,
        receiverDepth,
        encoding,
        minVariance,
        lightBleedReduction,
        positiveExponent,
        negativeExponent,
        mipLevel,
        false);
}

float XRENGINE_SampleShadowMoment2DArray(
    sampler2DArray shadowMap,
    vec2 uv,
    float layer,
    float receiverDepth,
    int encoding,
    float minVariance,
    float lightBleedReduction,
    float positiveExponent,
    float negativeExponent,
    float mipLevel)
{
    return XRENGINE_SampleShadowMoment2DArray(
        shadowMap,
        uv,
        layer,
        receiverDepth,
        encoding,
        minVariance,
        lightBleedReduction,
        positiveExponent,
        negativeExponent,
        mipLevel,
        false);
}

float XRENGINE_SampleShadowMomentCube(
    samplerCube shadowMap,
    vec3 direction,
    float receiverDepth,
    int encoding,
    float minVariance,
    float lightBleedReduction,
    float positiveExponent,
    float negativeExponent,
    float mipLevel)
{
    return XRENGINE_SampleShadowMomentCube(
        shadowMap,
        direction,
        receiverDepth,
        encoding,
        minVariance,
        lightBleedReduction,
        positiveExponent,
        negativeExponent,
        mipLevel,
        false);
}

float XRENGINE_EstimateShadowMomentMargin(
    sampler2D shadowMap,
    vec2 uv,
    float receiverDepth,
    int encoding,
    float positiveExponent,
    float negativeExponent,
    float mipLevel,
    bool useMipmaps)
{
    vec4 moments = XRENGINE_FetchShadowMoments2D(shadowMap, uv, mipLevel, useMipmaps);
    if (encoding == XRENGINE_SHADOW_ENCODING_VARIANCE2)
        return moments.x - receiverDepth;
    if (encoding == XRENGINE_SHADOW_ENCODING_EVSM2)
        return log(max(moments.x, 0.000001)) / max(positiveExponent, 0.0001) - receiverDepth;
    if (encoding == XRENGINE_SHADOW_ENCODING_EVSM4)
    {
        float positiveDepth = log(max(moments.x, 0.000001)) / max(positiveExponent, 0.0001);
        float negativeDepth = -log(max(-moments.z, 0.000001)) / max(negativeExponent, 0.0001);
        return min(positiveDepth - receiverDepth, negativeDepth - receiverDepth);
    }

    return moments.r - receiverDepth;
}

float XRENGINE_EstimateShadowMomentMargin(
    sampler2DArray shadowMap,
    vec2 uv,
    float layer,
    float receiverDepth,
    int encoding,
    float positiveExponent,
    float negativeExponent,
    float mipLevel,
    bool useMipmaps)
{
    vec4 moments = XRENGINE_FetchShadowMoments2DArray(shadowMap, uv, layer, mipLevel, useMipmaps);
    if (encoding == XRENGINE_SHADOW_ENCODING_VARIANCE2)
        return moments.x - receiverDepth;
    if (encoding == XRENGINE_SHADOW_ENCODING_EVSM2)
        return log(max(moments.x, 0.000001)) / max(positiveExponent, 0.0001) - receiverDepth;
    if (encoding == XRENGINE_SHADOW_ENCODING_EVSM4)
    {
        float positiveDepth = log(max(moments.x, 0.000001)) / max(positiveExponent, 0.0001);
        float negativeDepth = -log(max(-moments.z, 0.000001)) / max(negativeExponent, 0.0001);
        return min(positiveDepth - receiverDepth, negativeDepth - receiverDepth);
    }

    return moments.r - receiverDepth;
}

float XRENGINE_EstimateShadowMomentMargin(
    samplerCube shadowMap,
    vec3 direction,
    float receiverDepth,
    int encoding,
    float positiveExponent,
    float negativeExponent,
    float mipLevel,
    bool useMipmaps)
{
    vec4 moments = XRENGINE_FetchShadowMomentsCube(shadowMap, direction, mipLevel, useMipmaps);
    if (encoding == XRENGINE_SHADOW_ENCODING_VARIANCE2)
        return moments.x - receiverDepth;
    if (encoding == XRENGINE_SHADOW_ENCODING_EVSM2)
        return log(max(moments.x, 0.000001)) / max(positiveExponent, 0.0001) - receiverDepth;
    if (encoding == XRENGINE_SHADOW_ENCODING_EVSM4)
    {
        float positiveDepth = log(max(moments.x, 0.000001)) / max(positiveExponent, 0.0001);
        float negativeDepth = -log(max(-moments.z, 0.000001)) / max(negativeExponent, 0.0001);
        return min(positiveDepth - receiverDepth, negativeDepth - receiverDepth);
    }

    return moments.r - receiverDepth;
}

float XRENGINE_EstimateShadowMomentMargin(
    sampler2D shadowMap,
    vec2 uv,
    float receiverDepth,
    int encoding,
    float positiveExponent,
    float negativeExponent)
{
    return XRENGINE_EstimateShadowMomentMargin(
        shadowMap,
        uv,
        receiverDepth,
        encoding,
        positiveExponent,
        negativeExponent,
        0.0,
        false);
}

float XRENGINE_EstimateShadowMomentMargin(
    samplerCube shadowMap,
    vec3 direction,
    float receiverDepth,
    int encoding,
    float positiveExponent,
    float negativeExponent)
{
    return XRENGINE_EstimateShadowMomentMargin(
        shadowMap,
        direction,
        receiverDepth,
        encoding,
        positiveExponent,
        negativeExponent,
        0.0,
        false);
}

#endif
