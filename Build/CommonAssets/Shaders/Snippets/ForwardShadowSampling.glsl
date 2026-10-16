// Forward+ slots can vary per fragment. Keep literal sampler indices at each read.
// Callers validate the slot before filter evaluation.

vec4 XRENGINE_FetchShadowCubeTexel(int shadowMap, vec3 direction)
{
    switch (shadowMap)
    {
    case 0:
        return texture(PointLightShadowMaps[0], direction);
    case 1:
        return texture(PointLightShadowMaps[1], direction);
    case 2:
        return texture(PointLightShadowMaps[2], direction);
    case 3:
        return texture(PointLightShadowMaps[3], direction);
    default:
        return vec4(1.0);
    }
}

vec4 XRENGINE_FetchShadowMapTexel(int shadowMap, vec2 uv)
{
    switch (shadowMap)
    {
    case 0:
        return texture(SpotLightShadowMaps[0], uv);
    case 1:
        return texture(SpotLightShadowMaps[1], uv);
    case 2:
        return texture(SpotLightShadowMaps[2], uv);
    case 3:
        return texture(SpotLightShadowMaps[3], uv);
    default:
        return vec4(1.0);
    }
}

ivec2 XRENGINE_ShadowMapSize(int shadowMap)
{
    switch (shadowMap)
    {
    case 0:
        return textureSize(SpotLightShadowMaps[0], 0);
    case 1:
        return textureSize(SpotLightShadowMaps[1], 0);
    case 2:
        return textureSize(SpotLightShadowMaps[2], 0);
    case 3:
        return textureSize(SpotLightShadowMaps[3], 0);
    default:
        return ivec2(1);
    }
}

float XRENGINE_FetchShadowCubeDepth(int shadowMap, vec3 direction)
{
    return XRENGINE_FetchShadowCubeTexel(shadowMap, direction).r;
}

float XRENGINE_FetchShadowMapDepth(int shadowMap, vec2 uv)
{
    return XRENGINE_FetchShadowMapTexel(shadowMap, uv).r;
}

float XRENGINE_SampleShadowMapPCF(int shadowMap, vec3 shadowCoord, float bias, int kernelSize)
{
    XRENGINE_SHADOW_BODY_SampleShadowMapPCF
}

float XRENGINE_SampleShadowMapSoft(int shadowMap, vec3 shadowCoord, float bias, int sampleCount, float filterRadius)
{
    XRENGINE_SHADOW_BODY_SampleShadowMapSoft
}

float XRENGINE_SampleShadowMapVogel(int shadowMap, vec3 shadowCoord, float bias, int tapCount, float filterRadius)
{
    XRENGINE_SHADOW_BODY_SampleShadowMapVogel
}

float XRENGINE_SampleShadowMapTent4(int shadowMap, vec3 shadowCoord, float bias, float filterRadius)
{
    XRENGINE_SHADOW_BODY_SampleShadowMapTent4
}

float XRENGINE_SampleShadowCubeSimple(int shadowMap, vec3 shadowDir, float compareDepth, float bias, float farPlaneDist)
{
    XRENGINE_SHADOW_BODY_SampleShadowCubeSimple
}

float XRENGINE_SampleShadowCubePCF(int shadowMap, vec3 shadowDir, float compareDepth, float bias, float farPlaneDist, float sampleRadius)
{
    XRENGINE_SHADOW_BODY_SampleShadowCubePCF
}

float XRENGINE_SampleShadowCubeVogel(int shadowMap, vec3 shadowDir, float compareDepth, float bias, float farPlaneDist, float sampleRadius, int tapCount)
{
    XRENGINE_SHADOW_BODY_SampleShadowCubeVogel
}

float XRENGINE_BlockerSearch2D(int shadowMap, vec2 uv, float receiverDepth, float searchRadius, int sampleCount)
{
    XRENGINE_SHADOW_BODY_BlockerSearch2D
}

float XRENGINE_BlockerSearchCube(int shadowMap, vec3 shadowDir, float compareDepth, float farPlaneDist, float sampleRadius, int sampleCount)
{
    XRENGINE_SHADOW_BODY_BlockerSearchCube
}

float XRENGINE_ResolveShadowMapPenumbra(
    int shadowMap,
    vec3 shadowCoord,
    float bias,
    int blockerSamples,
    float searchRadius,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra)
{
    XRENGINE_SHADOW_BODY_ResolveShadowMapPenumbra
}

float XRENGINE_ResolveShadowCubePenumbra(
    int shadowMap,
    vec3 shadowDir,
    float compareDepth,
    float bias,
    float farPlaneDist,
    float blockerSearchRadius,
    int blockerSamples,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra)
{
    XRENGINE_SHADOW_BODY_ResolveShadowCubePenumbra
}

float XRENGINE_SampleShadowMapSimple(int shadowMap, vec3 shadowCoord, float bias)
{
    XRENGINE_SHADOW_BODY_SampleShadowMapSimple
}

float XRENGINE_SampleShadowMapFiltered(
    int shadowMap,
    vec3 shadowCoord,
    float bias,
    int blockerSamples,
    int filterSamples,
    float filterRadius,
    float blockerSearchRadius,
    int softShadowMode,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra,
    int vogelTapCount)
{
    XRENGINE_SHADOW_BODY_SampleShadowMapFiltered
}

float XRENGINE_SampleShadowCubeFiltered(
    int shadowMap,
    vec3 shadowDir,
    float compareDepth,
    float bias,
    float farPlaneDist,
    float sampleRadius,
    float blockerSearchRadius,
    int blockerSamples,
    int filterSamples,
    int softShadowMode,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra,
    int vogelTapCount)
{
    XRENGINE_SHADOW_BODY_SampleShadowCubeFiltered
}

// Keep implicit samples inside the original slot and mip branches.
// Evaluate the pure moment math once after the sampler selection.
vec4 XRENGINE_FetchShadowMomentsCube(int shadowSlot, vec3 direction, float mipLevel, bool useMipmaps)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_FetchShadowMomentsCube(PointLightShadowMaps[0], direction, mipLevel, useMipmaps);
    case 1:
        return XRENGINE_FetchShadowMomentsCube(PointLightShadowMaps[1], direction, mipLevel, useMipmaps);
    case 2:
        return XRENGINE_FetchShadowMomentsCube(PointLightShadowMaps[2], direction, mipLevel, useMipmaps);
    case 3:
        return XRENGINE_FetchShadowMomentsCube(PointLightShadowMaps[3], direction, mipLevel, useMipmaps);
    default:
        return vec4(1.0);
    }
}

float XRENGINE_SampleShadowMomentCube(
    int shadowSlot,
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
    vec4 moments = XRENGINE_FetchShadowMomentsCube(shadowSlot, direction, mipLevel, useMipmaps);
    return XRENGINE_EvaluateShadowMoments(
        moments, receiverDepth, encoding, minVariance, lightBleedReduction, positiveExponent, negativeExponent);
}

float XRENGINE_EstimateShadowMomentMargin(
    int shadowSlot,
    vec3 direction,
    float receiverDepth,
    int encoding,
    float positiveExponent,
    float negativeExponent,
    float mipLevel,
    bool useMipmaps)
{
    vec4 moments = XRENGINE_FetchShadowMomentsCube(shadowSlot, direction, mipLevel, useMipmaps);
    return XRENGINE_EvaluateShadowMomentMargin(
        moments, receiverDepth, encoding, positiveExponent, negativeExponent);
}

// Keep implicit samples inside the original slot and mip branches.
// Evaluate the pure moment math once after the sampler selection.
vec4 XRENGINE_FetchShadowMoments2D(int shadowSlot, vec2 uv, float mipLevel, bool useMipmaps)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_FetchShadowMoments2D(SpotLightShadowMaps[0], uv, mipLevel, useMipmaps);
    case 1:
        return XRENGINE_FetchShadowMoments2D(SpotLightShadowMaps[1], uv, mipLevel, useMipmaps);
    case 2:
        return XRENGINE_FetchShadowMoments2D(SpotLightShadowMaps[2], uv, mipLevel, useMipmaps);
    case 3:
        return XRENGINE_FetchShadowMoments2D(SpotLightShadowMaps[3], uv, mipLevel, useMipmaps);
    default:
        return vec4(1.0);
    }
}

float XRENGINE_SampleShadowMoment2D(
    int shadowSlot,
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
    vec4 moments = XRENGINE_FetchShadowMoments2D(shadowSlot, uv, mipLevel, useMipmaps);
    return XRENGINE_EvaluateShadowMoments(
        moments, receiverDepth, encoding, minVariance, lightBleedReduction, positiveExponent, negativeExponent);
}

float XRENGINE_EstimateShadowMomentMargin(
    int shadowSlot,
    vec2 uv,
    float receiverDepth,
    int encoding,
    float positiveExponent,
    float negativeExponent,
    float mipLevel,
    bool useMipmaps)
{
    vec4 moments = XRENGINE_FetchShadowMoments2D(shadowSlot, uv, mipLevel, useMipmaps);
    return XRENGINE_EvaluateShadowMomentMargin(
        moments, receiverDepth, encoding, positiveExponent, negativeExponent);
}

// Keep receiver derivatives under the same per-slot control flow.
float XRENGINE_ComputeSpotShadowDepthBiasSlot(
    int shadowSlot, vec3 shadowCoord, vec2 texelSize, float filterRadius,
    float constantDepthBias, float slopeBiasTexels)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_ComputeShadowDepthBias(shadowCoord, texelSize, filterRadius, constantDepthBias, slopeBiasTexels);
    case 1:
        return XRENGINE_ComputeShadowDepthBias(shadowCoord, texelSize, filterRadius, constantDepthBias, slopeBiasTexels);
    case 2:
        return XRENGINE_ComputeShadowDepthBias(shadowCoord, texelSize, filterRadius, constantDepthBias, slopeBiasTexels);
    case 3:
        return XRENGINE_ComputeShadowDepthBias(shadowCoord, texelSize, filterRadius, constantDepthBias, slopeBiasTexels);
    default:
        return 0.0;
    }
}

float XRENGINE_SampleContactShadow2D(
    int shadowMap,
    mat4 lightMatrix,
    vec3 fragPosWS,
    vec3 normalWS,
    vec3 lightDirWS,
    float receiverOffset,
    float compareBias,
    float contactDistance,
    int contactSamples,
    float contactThickness,
    float contactFadeStart,
    float contactFadeEnd,
    float contactNormalOffset,
    float jitterStrength,
    float viewDepth)
{
    XRENGINE_SHADOW_BODY_SampleContactShadow2D
}

float XRENGINE_SampleContactShadowCube(
    int shadowMap,
    vec3 fragPosWS,
    vec3 normalWS,
    vec3 lightPosWS,
    float receiverOffset,
    float compareBias,
    float farPlaneDist,
    float contactDistance,
    int contactSamples,
    float contactThickness,
    float contactFadeStart,
    float contactFadeEnd,
    float contactNormalOffset,
    float jitterStrength,
    float viewDepth)
{
    XRENGINE_SHADOW_BODY_SampleContactShadowCube
}
