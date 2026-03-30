// Shadow Sampling Utilities Snippet

vec3 XRENGINE_ProjectShadowCoord(mat4 lightMatrix, vec3 fragPosWS)
{
    vec4 fragPosLightSpace = lightMatrix * vec4(fragPosWS, 1.0);
    vec3 shadowCoord = fragPosLightSpace.xyz / fragPosLightSpace.w;
    return shadowCoord * 0.5 + 0.5;
}

bool XRENGINE_ShadowCoordInBounds(vec3 shadowCoord)
{
    return shadowCoord.x >= 0.0 && shadowCoord.x <= 1.0 &&
           shadowCoord.y >= 0.0 && shadowCoord.y <= 1.0 &&
           shadowCoord.z >= 0.0 && shadowCoord.z <= 1.0;
}

int XRENGINE_ResolveContactShadowSampleCount(int requestedSamples, float viewDepth, float contactDistance)
{
    if (requestedSamples <= 0 || contactDistance <= 0.0)
        return 0;

    int clampedSamples = clamp(requestedSamples, 1, 32);
    float normalizedDepth = max(viewDepth, contactDistance);
    float depthScale = clamp((contactDistance * 24.0) / normalizedDepth, 0.35, 1.0);
    return max(1, int(ceil(float(clampedSamples) * depthScale)));
}

float XRENGINE_SampleShadowMapPCF(sampler2D shadowMap, vec3 shadowCoord, float bias, int kernelSize)
{
    float shadow = 0.0;
    vec2 texelSize = 1.0 / vec2(textureSize(shadowMap, 0));
    int halfKernel = kernelSize / 2;
    float sampleCount = float(kernelSize * kernelSize);

    for (int x = -halfKernel; x <= halfKernel; ++x)
    {
        for (int y = -halfKernel; y <= halfKernel; ++y)
        {
            float pcfDepth = texture(shadowMap, shadowCoord.xy + vec2(x, y) * texelSize).r;
            shadow += (shadowCoord.z - bias) > pcfDepth ? 0.0 : 1.0;
        }
    }

    return shadow / sampleCount;
}

float XRENGINE_SampleShadowMapPCFAddBias(sampler2D shadowMap, vec3 shadowCoord, float bias, int kernelSize)
{
    float shadow = 0.0;
    vec2 texelSize = 1.0 / vec2(textureSize(shadowMap, 0));
    int halfKernel = kernelSize / 2;
    float sampleCount = float(kernelSize * kernelSize);

    for (int x = -halfKernel; x <= halfKernel; ++x)
    {
        for (int y = -halfKernel; y <= halfKernel; ++y)
        {
            float pcfDepth = texture(shadowMap, shadowCoord.xy + vec2(x, y) * texelSize).r;
            shadow += (shadowCoord.z + bias) <= pcfDepth ? 1.0 : 0.0;
        }
    }

    return shadow / sampleCount;
}

float XRENGINE_SampleShadowMapArrayPCF(sampler2DArray shadowMap, vec3 shadowCoord, float layer, float bias, int kernelSize)
{
    float shadow = 0.0;
    vec2 texelSize = 1.0 / vec2(textureSize(shadowMap, 0).xy);
    int halfKernel = kernelSize / 2;
    float sampleCount = float(kernelSize * kernelSize);

    for (int x = -halfKernel; x <= halfKernel; ++x)
    {
        for (int y = -halfKernel; y <= halfKernel; ++y)
        {
            float pcfDepth = texture(shadowMap, vec3(shadowCoord.xy + vec2(x, y) * texelSize, layer)).r;
            shadow += (shadowCoord.z - bias) > pcfDepth ? 0.0 : 1.0;
        }
    }

    return shadow / sampleCount;
}

float XRENGINE_SampleShadowMapArraySimple(sampler2DArray shadowMap, vec3 shadowCoord, float layer, float bias)
{
    float depth = texture(shadowMap, vec3(shadowCoord.xy, layer)).r;
    return (shadowCoord.z - bias) > depth ? 0.0 : 1.0;
}

const vec2 XRENGINE_ShadowPoissonDisk[16] = vec2[](
    vec2(-0.94201624, -0.39906216), vec2(0.94558609, -0.76890725),
    vec2(-0.09418410, -0.92938870), vec2(0.34495938, 0.29387760),
    vec2(-0.91588581, 0.45771432), vec2(-0.81544232, -0.87912464),
    vec2(-0.38277543, 0.27676845), vec2(0.97484398, 0.75648379),
    vec2(0.44323325, -0.97511554), vec2(0.53742981, -0.47373420),
    vec2(-0.26496911, -0.41893023), vec2(0.79197514, 0.19090188),
    vec2(-0.24188840, 0.99706507), vec2(-0.81409955, 0.91437590),
    vec2(0.19984126, 0.78641367), vec2(0.14383161, -0.14100790)
);

float XRENGINE_SampleShadowMapSoft(sampler2D shadowMap, vec3 shadowCoord, float bias, int sampleCount, float filterRadius)
{
    int clampedSamples = clamp(sampleCount, 1, 16);
    vec2 texelSize = 1.0 / vec2(textureSize(shadowMap, 0));
    float radius = max(filterRadius, max(texelSize.x, texelSize.y));
    float lit = 0.0;

    for (int i = 0; i < 16; ++i)
    {
        if (i >= clampedSamples)
            break;

        vec2 sampleUv = shadowCoord.xy + XRENGINE_ShadowPoissonDisk[i] * radius;
        float sampleDepth = texture(shadowMap, sampleUv).r;
        lit += (shadowCoord.z - bias) <= sampleDepth ? 1.0 : 0.0;
    }

    return lit / float(clampedSamples);
}

float XRENGINE_SampleShadowMapTent4(sampler2D shadowMap, vec3 shadowCoord, float bias, float filterRadius)
{
    vec2 texelSize = 1.0 / vec2(textureSize(shadowMap, 0));
    vec2 radius = max(vec2(max(filterRadius, 0.0)), texelSize);
    float lit = 0.0;

    lit += (shadowCoord.z - bias) <= texture(shadowMap, shadowCoord.xy + vec2(-0.5, -0.5) * radius).r ? 1.0 : 0.0;
    lit += (shadowCoord.z - bias) <= texture(shadowMap, shadowCoord.xy + vec2(0.5, -0.5) * radius).r ? 1.0 : 0.0;
    lit += (shadowCoord.z - bias) <= texture(shadowMap, shadowCoord.xy + vec2(-0.5, 0.5) * radius).r ? 1.0 : 0.0;
    lit += (shadowCoord.z - bias) <= texture(shadowMap, shadowCoord.xy + vec2(0.5, 0.5) * radius).r ? 1.0 : 0.0;

    return lit * 0.25;
}

float XRENGINE_SampleShadowMapArrayTent4(sampler2DArray shadowMap, vec3 shadowCoord, float layer, float bias, float filterRadius)
{
    vec2 texelSize = 1.0 / vec2(textureSize(shadowMap, 0).xy);
    vec2 radius = max(vec2(max(filterRadius, 0.0)), texelSize);
    float lit = 0.0;

    lit += (shadowCoord.z - bias) <= texture(shadowMap, vec3(shadowCoord.xy + vec2(-0.5, -0.5) * radius, layer)).r ? 1.0 : 0.0;
    lit += (shadowCoord.z - bias) <= texture(shadowMap, vec3(shadowCoord.xy + vec2(0.5, -0.5) * radius, layer)).r ? 1.0 : 0.0;
    lit += (shadowCoord.z - bias) <= texture(shadowMap, vec3(shadowCoord.xy + vec2(-0.5, 0.5) * radius, layer)).r ? 1.0 : 0.0;
    lit += (shadowCoord.z - bias) <= texture(shadowMap, vec3(shadowCoord.xy + vec2(0.5, 0.5) * radius, layer)).r ? 1.0 : 0.0;

    return lit * 0.25;
}

float XRENGINE_SampleShadowMapArraySoft(sampler2DArray shadowMap, vec3 shadowCoord, float layer, float bias, int sampleCount, float filterRadius)
{
    int clampedSamples = clamp(sampleCount, 1, 16);
    vec2 texelSize = 1.0 / vec2(textureSize(shadowMap, 0).xy);
    float radius = max(filterRadius, max(texelSize.x, texelSize.y));
    float lit = 0.0;

    for (int i = 0; i < 16; ++i)
    {
        if (i >= clampedSamples)
            break;

        vec2 sampleUv = shadowCoord.xy + XRENGINE_ShadowPoissonDisk[i] * radius;
        float sampleDepth = texture(shadowMap, vec3(sampleUv, layer)).r;
        lit += (shadowCoord.z - bias) <= sampleDepth ? 1.0 : 0.0;
    }

    return lit / float(clampedSamples);
}

float XRENGINE_SampleContactShadow2D(
    sampler2D shadowMap,
    mat4 lightMatrix,
    vec3 fragPosWS,
    vec3 normalWS,
    vec3 lightDirWS,
    float receiverOffset,
    float compareBias,
    float contactDistance,
    int contactSamples)
{
    if (contactDistance <= 0.0 || contactSamples <= 0)
        return 1.0;

    int clampedSamples = clamp(contactSamples, 1, 32);
    float stepSize = contactDistance / float(clampedSamples);
    vec3 rayOrigin = fragPosWS + normalWS * max(receiverOffset, compareBias * 2.0);

    // Interleaved gradient noise (Jimenez 2014) — temporally stable dithering
    float noise = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));

    float occlusion = 0.0;

    for (int i = 0; i < 32; ++i)
    {
        if (i >= clampedSamples)
            break;

        // Dithered step: center of interval + noise offset
        float travel = (float(i) + 0.5 + (noise - 0.5)) * stepSize;
        vec3 samplePosWS = rayOrigin + lightDirWS * travel;
        vec3 shadowCoord = XRENGINE_ProjectShadowCoord(lightMatrix, samplePosWS);
        if (!XRENGINE_ShadowCoordInBounds(shadowCoord))
            continue;

        float shadowDepth = texture(shadowMap, shadowCoord.xy).r;
        float blockerDelta = shadowCoord.z - shadowDepth;
        if (blockerDelta <= compareBias)
            continue;

        // Proportional thickness: stable regardless of shadow map resolution
        float thickness = max(travel * 0.25, compareBias * 4.0);
        if (blockerDelta > thickness)
            continue;

        float depthWeight = 1.0 - smoothstep(compareBias, thickness, blockerDelta);
        float distWeight = 1.0 - (travel / contactDistance);
        occlusion = max(occlusion, depthWeight * distWeight);
    }

    return 1.0 - occlusion;
}

float XRENGINE_SampleContactShadowArray(
    sampler2DArray shadowMap,
    mat4 lightMatrix,
    float layer,
    vec3 fragPosWS,
    vec3 normalWS,
    vec3 lightDirWS,
    float receiverOffset,
    float compareBias,
    float contactDistance,
    int contactSamples)
{
    if (contactDistance <= 0.0 || contactSamples <= 0)
        return 1.0;

    int clampedSamples = clamp(contactSamples, 1, 32);
    float stepSize = contactDistance / float(clampedSamples);
    vec3 rayOrigin = fragPosWS + normalWS * max(receiverOffset, compareBias * 2.0);

    // Interleaved gradient noise (Jimenez 2014) — temporally stable dithering
    float noise = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));

    float occlusion = 0.0;

    for (int i = 0; i < 32; ++i)
    {
        if (i >= clampedSamples)
            break;

        float travel = (float(i) + 0.5 + (noise - 0.5)) * stepSize;
        vec3 samplePosWS = rayOrigin + lightDirWS * travel;
        vec3 shadowCoord = XRENGINE_ProjectShadowCoord(lightMatrix, samplePosWS);
        if (!XRENGINE_ShadowCoordInBounds(shadowCoord))
            continue;

        float shadowDepth = texture(shadowMap, vec3(shadowCoord.xy, layer)).r;
        float blockerDelta = shadowCoord.z - shadowDepth;
        if (blockerDelta <= compareBias)
            continue;

        float thickness = max(travel * 0.25, compareBias * 4.0);
        if (blockerDelta > thickness)
            continue;

        float depthWeight = 1.0 - smoothstep(compareBias, thickness, blockerDelta);
        float distWeight = 1.0 - (travel / contactDistance);
        occlusion = max(occlusion, depthWeight * distWeight);
    }

    return 1.0 - occlusion;
}

float XRENGINE_SampleShadowMapSimple(sampler2D shadowMap, vec3 shadowCoord, float bias)
{
    float depth = texture(shadowMap, shadowCoord.xy).r;
    return (shadowCoord.z - bias) > depth ? 0.0 : 1.0;
}

float XRENGINE_SampleShadowMapSimpleAddBias(sampler2D shadowMap, vec3 shadowCoord, float bias)
{
    float depth = texture(shadowMap, shadowCoord.xy).r;
    return (shadowCoord.z + bias) <= depth ? 1.0 : 0.0;
}

float XRENGINE_SampleShadowMapFiltered(sampler2D shadowMap, vec3 shadowCoord, float bias, int requestedSamples, float filterRadius, bool enableSoft)
{
    int sampleCount = clamp(requestedSamples, 1, 16);
    if (sampleCount <= 1)
        return XRENGINE_SampleShadowMapSimple(shadowMap, shadowCoord, bias);

    if (enableSoft)
        return XRENGINE_SampleShadowMapSoft(shadowMap, shadowCoord, bias, sampleCount, filterRadius);

    if (sampleCount <= 4)
        return XRENGINE_SampleShadowMapTent4(shadowMap, shadowCoord, bias, filterRadius);

    return XRENGINE_SampleShadowMapPCF(shadowMap, shadowCoord, bias, 3);
}

float XRENGINE_SampleShadowMapArrayFiltered(sampler2DArray shadowMap, vec3 shadowCoord, float layer, float bias, int requestedSamples, float filterRadius, bool enableSoft)
{
    int sampleCount = clamp(requestedSamples, 1, 16);
    if (sampleCount <= 1)
        return XRENGINE_SampleShadowMapArraySimple(shadowMap, shadowCoord, layer, bias);

    if (enableSoft)
        return XRENGINE_SampleShadowMapArraySoft(shadowMap, shadowCoord, layer, bias, sampleCount, filterRadius);

    if (sampleCount <= 4)
        return XRENGINE_SampleShadowMapArrayTent4(shadowMap, shadowCoord, layer, bias, filterRadius);

    return XRENGINE_SampleShadowMapArrayPCF(shadowMap, shadowCoord, layer, bias, 3);
}

const vec3 XRENGINE_ShadowCubeKernel[20] = vec3[](
    vec3( 1.0,  1.0,  1.0), vec3( 1.0, -1.0,  1.0),
    vec3(-1.0,  1.0,  1.0), vec3(-1.0, -1.0,  1.0),
    vec3( 1.0,  1.0, -1.0), vec3( 1.0, -1.0, -1.0),
    vec3(-1.0,  1.0, -1.0), vec3(-1.0, -1.0, -1.0),
    vec3( 1.0,  0.0,  0.0), vec3(-1.0,  0.0,  0.0),
    vec3( 0.0,  1.0,  0.0), vec3( 0.0, -1.0,  0.0),
    vec3( 0.0,  0.0,  1.0), vec3( 0.0,  0.0, -1.0),
    vec3( 1.0,  1.0,  0.0), vec3( 1.0, -1.0,  0.0),
    vec3(-1.0,  1.0,  0.0), vec3(-1.0, -1.0,  0.0),
    vec3( 0.0,  1.0,  1.0), vec3( 0.0, -1.0, -1.0)
);

const int XRENGINE_ShadowCubeKernelTapOrder[20] = int[](
    0, 3, 5, 6,
    7, 4, 2, 1,
    8, 9, 10, 11,
    12, 13, 14, 17,
    15, 16, 18, 19
);

vec3 XRENGINE_GetShadowCubeKernelTap(int tapIndex)
{
    return XRENGINE_ShadowCubeKernel[XRENGINE_ShadowCubeKernelTapOrder[tapIndex]];
}

float XRENGINE_SampleShadowCubeSimple(samplerCube shadowMap, vec3 shadowDir, float compareDepth, float bias, float farPlaneDist)
{
    float sampleDepth = texture(shadowMap, normalize(shadowDir)).r * farPlaneDist;
    return (compareDepth - bias) <= sampleDepth ? 1.0 : 0.0;
}

float XRENGINE_SampleShadowCubePCF(samplerCube shadowMap, vec3 shadowDir, float compareDepth, float bias, float farPlaneDist, float sampleRadius)
{
    float lit = 0.0;

    for (int i = 0; i < 20; ++i)
    {
        vec3 sampleDir = normalize(shadowDir + XRENGINE_GetShadowCubeKernelTap(i) * sampleRadius);
        float sampleDepth = texture(shadowMap, sampleDir).r * farPlaneDist;
        lit += (compareDepth - bias) <= sampleDepth ? 1.0 : 0.0;
    }

    return lit / 20.0;
}

float XRENGINE_SampleShadowCubeSoft(samplerCube shadowMap, vec3 shadowDir, float compareDepth, float bias, float farPlaneDist, float sampleRadius, int sampleCount)
{
    int clampedSamples = clamp(sampleCount, 1, 20);
    float lit = 0.0;

    for (int i = 0; i < 20; ++i)
    {
        if (i >= clampedSamples)
            break;

        vec3 sampleDir = normalize(shadowDir + XRENGINE_GetShadowCubeKernelTap(i) * sampleRadius);
        float sampleDepth = texture(shadowMap, sampleDir).r * farPlaneDist;
        lit += (compareDepth - bias) <= sampleDepth ? 1.0 : 0.0;
    }

    return lit / float(clampedSamples);
}

float XRENGINE_SampleShadowCubeFiltered(samplerCube shadowMap, vec3 shadowDir, float compareDepth, float bias, float farPlaneDist, float sampleRadius, int requestedSamples, bool enableSoft)
{
    int sampleCount = clamp(requestedSamples, 1, 20);
    if (sampleCount <= 1)
        return XRENGINE_SampleShadowCubeSimple(shadowMap, shadowDir, compareDepth, bias, farPlaneDist);

    if (enableSoft || sampleCount <= 4)
        return XRENGINE_SampleShadowCubeSoft(shadowMap, shadowDir, compareDepth, bias, farPlaneDist, sampleRadius, sampleCount);

    return XRENGINE_SampleShadowCubePCF(shadowMap, shadowDir, compareDepth, bias, farPlaneDist, sampleRadius);
}

float XRENGINE_BlendShadowFilter(float hardShadow, float filteredShadow, float depthDelta, float maxBlurDist)
{
    float normDist = clamp(depthDelta, 0.0, maxBlurDist) / maxBlurDist;
    return mix(hardShadow, filteredShadow, normDist);
}

float XRENGINE_CalculateShadowBias(float NdotL, float maxBias, float minBias)
{
    return max(maxBias * (1.0 - NdotL), minBias);
}
