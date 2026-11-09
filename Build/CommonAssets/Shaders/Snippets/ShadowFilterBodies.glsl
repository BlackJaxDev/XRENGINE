// Shared bodies for native samplers and forward texture selectors.
// Each wrapper must use the same parameter names as its native overload.
// Texture adapters select the source; filter math and loop guards stay here.
// Keep ordinary function wrappers so source trimming can find each overload.

#define XRENGINE_SHADOW_BODY_SampleShadowMapPCF \
    float shadow = 0.0; \
    vec2 texelSize = 1.0 / vec2(XRENGINE_ShadowMapSize(shadowMap)); \
    int halfKernel = kernelSize / 2; \
    float sampleCount = float(kernelSize * kernelSize); \
 \
    for (int x = -halfKernel; x <= halfKernel; ++x) \
    { \
        for (int y = -halfKernel; y <= halfKernel; ++y) \
        { \
            float pcfDepth = XRENGINE_FetchShadowMapTexel(shadowMap, shadowCoord.xy + vec2(x, y) * texelSize).r; \
            shadow += XRENGINE_ShadowLit(shadowCoord.z, pcfDepth, bias); \
        } \
    } \
 \
    return shadow / sampleCount;

#define XRENGINE_SHADOW_BODY_SampleShadowMapSoft \
    int clampedSamples = clamp(sampleCount, 1, 16); \
    vec2 texelSize = 1.0 / vec2(XRENGINE_ShadowMapSize(shadowMap)); \
    float radius = max(filterRadius, max(texelSize.x, texelSize.y)); \
    float lit = 0.0; \
 \
    for (int i = 0; i < 16; ++i) \
    { \
        if (i >= clampedSamples) \
            break; \
 \
        vec2 sampleUv = shadowCoord.xy + XRENGINE_ShadowPoissonDisk[i] * radius; \
        float sampleDepth = XRENGINE_FetchShadowMapTexel(shadowMap, sampleUv).r; \
        lit += XRENGINE_ShadowLit(shadowCoord.z, sampleDepth, bias); \
    } \
 \
    return lit / float(clampedSamples);

#define XRENGINE_SHADOW_BODY_SampleShadowMapVogel \
    int clampedTaps = clamp(tapCount, 1, XRENGINE_MaxVogelShadowTaps); \
    if (clampedTaps <= 1) \
    { \
        float depth = XRENGINE_FetchShadowMapTexel(shadowMap, shadowCoord.xy).r; \
        return XRENGINE_ShadowLit(shadowCoord.z, depth, bias); \
    } \
 \
    vec2 texelSize = 1.0 / vec2(XRENGINE_ShadowMapSize(shadowMap)); \
    float radius = max(filterRadius, max(texelSize.x, texelSize.y)); \
    float lit = 0.0; \
    float rotation = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy) * XRENGINE_ShadowPi * 2.0; \
 \
    for (int i = 0; i < XRENGINE_MaxVogelShadowTaps; ++i) \
    { \
        if (i >= clampedTaps) \
            break; \
 \
        vec2 sampleUv = shadowCoord.xy + XRENGINE_GetVogelDiskTapRotated(i, clampedTaps, rotation) * radius; \
        float sampleDepth = XRENGINE_FetchShadowMapTexel(shadowMap, sampleUv).r; \
        lit += XRENGINE_ShadowLit(shadowCoord.z, sampleDepth, bias); \
    } \
 \
    return lit / float(clampedTaps);

#define XRENGINE_SHADOW_BODY_SampleShadowMapTent4 \
    vec2 texelSize = 1.0 / vec2(XRENGINE_ShadowMapSize(shadowMap)); \
    vec2 radius = max(vec2(max(filterRadius, 0.0)), texelSize); \
    float lit = 0.0; \
 \
    lit += XRENGINE_ShadowLit(shadowCoord.z, XRENGINE_FetchShadowMapTexel(shadowMap, shadowCoord.xy + vec2(-0.5, -0.5) * radius).r, bias); \
    lit += XRENGINE_ShadowLit(shadowCoord.z, XRENGINE_FetchShadowMapTexel(shadowMap, shadowCoord.xy + vec2(0.5, -0.5) * radius).r, bias); \
    lit += XRENGINE_ShadowLit(shadowCoord.z, XRENGINE_FetchShadowMapTexel(shadowMap, shadowCoord.xy + vec2(-0.5, 0.5) * radius).r, bias); \
    lit += XRENGINE_ShadowLit(shadowCoord.z, XRENGINE_FetchShadowMapTexel(shadowMap, shadowCoord.xy + vec2(0.5, 0.5) * radius).r, bias); \
 \
    return lit * 0.25;

#define XRENGINE_SHADOW_BODY_SampleShadowCubeSimple \
    float sampleDepth = XRENGINE_FetchShadowCubeTexel(shadowMap, normalize(shadowDir)).r * farPlaneDist; \
    return (compareDepth - bias) <= sampleDepth ? 1.0 : 0.0;

#define XRENGINE_SHADOW_BODY_SampleShadowCubePCF \
    float lit = 0.0; \
 \
    for (int i = 0; i < 20; ++i) \
    { \
        vec3 sampleDir = normalize(shadowDir + XRENGINE_GetShadowCubeKernelTap(i) * sampleRadius); \
        float sampleDepth = XRENGINE_FetchShadowCubeTexel(shadowMap, sampleDir).r * farPlaneDist; \
        lit += (compareDepth - bias) <= sampleDepth ? 1.0 : 0.0; \
    } \
 \
    return lit / 20.0;

#define XRENGINE_SHADOW_BODY_SampleShadowCubeVogel \
    int clampedTaps = clamp(tapCount, 1, XRENGINE_MaxVogelShadowTaps); \
    if (clampedTaps <= 1) \
        return XRENGINE_SampleShadowCubeSimple(shadowMap, shadowDir, compareDepth, bias, farPlaneDist); \
 \
    vec3 baseDir = normalize(shadowDir); \
    vec3 tangent; \
    vec3 bitangent; \
    XRENGINE_BuildOrthonormalBasis(baseDir, tangent, bitangent); \
    float lit = 0.0; \
    float rotation = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy) * XRENGINE_ShadowPi * 2.0; \
 \
    for (int i = 0; i < XRENGINE_MaxVogelShadowTaps; ++i) \
    { \
        if (i >= clampedTaps) \
            break; \
 \
        vec2 diskTap = XRENGINE_GetVogelDiskTapRotated(i, clampedTaps, rotation) * sampleRadius; \
        vec3 sampleDir = normalize(baseDir + tangent * diskTap.x + bitangent * diskTap.y); \
        float sampleDepth = XRENGINE_FetchShadowCubeTexel(shadowMap, sampleDir).r * farPlaneDist; \
        lit += (compareDepth - bias) <= sampleDepth ? 1.0 : 0.0; \
    } \
 \
    return lit / float(clampedTaps);

#define XRENGINE_SHADOW_BODY_BlockerSearch2D \
    float blockerSum = 0.0; \
    int blockerCount = 0; \
    int clampedSamples = clamp(sampleCount, 1, XRENGINE_MaxVogelShadowTaps); \
    vec2 texelSize = 1.0 / vec2(XRENGINE_ShadowMapSize(shadowMap)); \
    float radius = max(searchRadius, max(texelSize.x, texelSize.y)); \
    float rotation = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy) * XRENGINE_ShadowPi * 2.0; \
 \
    for (int i = 0; i < XRENGINE_MaxVogelShadowTaps; ++i) \
    { \
        if (i >= clampedSamples) \
            break; \
 \
        vec2 sampleUv = uv + XRENGINE_GetVogelDiskTapRotated(i, clampedSamples, rotation) * radius; \
        float sampleDepth = XRENGINE_FetchShadowMapTexel(shadowMap, sampleUv).r; \
        if (XRENGINE_IsShadowBlocker(sampleDepth, receiverDepth)) \
        { \
            blockerSum += sampleDepth; \
            blockerCount++; \
        } \
    } \
 \
    return blockerCount > 0 ? blockerSum / float(blockerCount) : -1.0;

#define XRENGINE_SHADOW_BODY_BlockerSearchCube \
    float blockerSum = 0.0; \
    int blockerCount = 0; \
    int clampedSamples = clamp(sampleCount, 1, XRENGINE_MaxVogelShadowTaps); \
    vec3 baseDir = normalize(shadowDir); \
    vec3 tangent; \
    vec3 bitangent; \
    XRENGINE_BuildOrthonormalBasis(baseDir, tangent, bitangent); \
    float radius = max(sampleRadius, 0.000001); \
    float rotation = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy) * XRENGINE_ShadowPi * 2.0; \
 \
    for (int i = 0; i < XRENGINE_MaxVogelShadowTaps; ++i) \
    { \
        if (i >= clampedSamples) \
            break; \
 \
        vec2 diskTap = XRENGINE_GetVogelDiskTapRotated(i, clampedSamples, rotation) * radius; \
        vec3 sampleDir = normalize(baseDir + tangent * diskTap.x + bitangent * diskTap.y); \
        float sampleDepth = XRENGINE_FetchShadowCubeTexel(shadowMap, sampleDir).r * farPlaneDist; \
        if (sampleDepth < compareDepth) \
        { \
            blockerSum += sampleDepth; \
            blockerCount++; \
        } \
    } \
 \
    return blockerCount > 0 ? blockerSum / float(blockerCount) : -1.0;

#define XRENGINE_SHADOW_BODY_ResolveShadowMapPenumbra \
    float receiverDepth = XRENGINE_ApplyShadowBias(shadowCoord.z, bias); \
    float avgBlockerDepth = XRENGINE_BlockerSearch2D(shadowMap, shadowCoord.xy, receiverDepth, searchRadius, blockerSamples); \
 \
    if (avgBlockerDepth < 0.0) \
        return -1.0; \
 \
    vec2 texelSize = 1.0 / vec2(XRENGINE_ShadowMapSize(shadowMap)); \
    float minRadius = max(minPenumbra, max(texelSize.x, texelSize.y)); \
    return XRENGINE_EstimatePenumbra(receiverDepth, avgBlockerDepth, lightSourceRadius, minRadius, max(maxPenumbra, minRadius));

#define XRENGINE_SHADOW_BODY_ResolveShadowCubePenumbra \
    float receiverDepth = compareDepth - bias; \
    float avgBlockerDepth = XRENGINE_BlockerSearchCube(shadowMap, shadowDir, receiverDepth, farPlaneDist, blockerSearchRadius, blockerSamples); \
 \
    if (avgBlockerDepth < 0.0) \
        return -1.0; \
 \
    float angularSourceRadius = max(lightSourceRadius, 0.0) / max(avgBlockerDepth, 0.0001); \
    float rawPenumbra = (receiverDepth - avgBlockerDepth) / max(avgBlockerDepth, 0.0001) * angularSourceRadius; \
    return XRENGINE_ClampPenumbra(rawPenumbra, max(minPenumbra, 0.000001), max(maxPenumbra, minPenumbra));

#define XRENGINE_SHADOW_BODY_SampleShadowMapSimple \
    float depth = XRENGINE_FetchShadowMapTexel(shadowMap, shadowCoord.xy).r; \
    return XRENGINE_ShadowLit(shadowCoord.z, depth, bias);

#define XRENGINE_SHADOW_BODY_SampleShadowMapFiltered \
    int tapCount = vogelTapCount; \
    float radius = filterRadius; \
    if (softShadowMode != 3) /* VogelDisk uses its own tap count. */ \
    { \
        tapCount = clamp(filterSamples, 1, XRENGINE_MaxVogelShadowTaps); \
        if (tapCount <= 1) \
            return XRENGINE_SampleShadowMapSimple(shadowMap, shadowCoord, bias); \
 \
        if (softShadowMode == 2) /* ContactHardeningPcss */ \
        { \
            radius = XRENGINE_ResolveShadowMapPenumbra( \
                shadowMap, shadowCoord, bias, blockerSamples, blockerSearchRadius, \
                lightSourceRadius, minPenumbra, maxPenumbra); \
            if (radius < 0.0) \
                return 1.0; \
        } \
        else \
        { \
            if (softShadowMode == 1) /* FixedPoisson */ \
                return XRENGINE_SampleShadowMapSoft(shadowMap, shadowCoord, bias, tapCount, filterRadius); \
            if (tapCount <= 4) \
                return XRENGINE_SampleShadowMapTent4(shadowMap, shadowCoord, bias, filterRadius); \
            return XRENGINE_SampleShadowMapPCF(shadowMap, shadowCoord, bias, 3); \
        } \
    } \
 \
    return XRENGINE_SampleShadowMapVogel(shadowMap, shadowCoord, bias, tapCount, radius);

#define XRENGINE_SHADOW_BODY_SampleShadowCubeFiltered \
    /* Select the filter inputs before one shared Vogel loop. Sampler-slot */ \
    /* dispatch must not expand a separate loop for each soft-shadow mode. */ \
    int tapCount = vogelTapCount; \
    float radius = sampleRadius; \
    if (softShadowMode != 3) /* VogelDisk uses its own tap count. */ \
    { \
        tapCount = clamp(filterSamples, 1, XRENGINE_MaxVogelShadowTaps); \
        if (tapCount <= 1) \
            return XRENGINE_SampleShadowCubeSimple(shadowMap, shadowDir, compareDepth, bias, farPlaneDist); \
 \
        if (softShadowMode == 2) /* ContactHardeningPcss */ \
        { \
            radius = XRENGINE_ResolveShadowCubePenumbra( \
                shadowMap, shadowDir, compareDepth, bias, farPlaneDist, \
                blockerSearchRadius, blockerSamples, lightSourceRadius, minPenumbra, maxPenumbra); \
            if (radius < 0.0) \
                return 1.0; \
        } \
        else if (softShadowMode != 1 && tapCount > 4) \
        { \
            return XRENGINE_SampleShadowCubePCF(shadowMap, shadowDir, compareDepth, bias, farPlaneDist, sampleRadius); \
        } \
    } \
 \
    return XRENGINE_SampleShadowCubeVogel(shadowMap, shadowDir, compareDepth, bias, farPlaneDist, radius, tapCount);

#define XRENGINE_SHADOW_BODY_SampleContactShadow2D \
    if (contactDistance <= 0.0 || contactSamples <= 0) \
        return 1.0; \
 \
    float fade = contactFadeEnd > contactFadeStart \
        ? 1.0 - smoothstep(contactFadeStart, contactFadeEnd, viewDepth) \
        : 1.0; \
    fade *= smoothstep(0.05, 0.35, max(dot(normalize(normalWS), normalize(lightDirWS)), 0.0)); \
    if (fade <= 0.0) \
        return 1.0; \
 \
    int clampedSamples = clamp(contactSamples, 1, 32); \
    float stepSize = contactDistance / float(clampedSamples); \
    vec3 rayOrigin = fragPosWS + normalWS * max(max(receiverOffset, contactNormalOffset), compareBias * 2.0); \
 \
    float noise = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy); \
 \
    float occlusion = 0.0; \
 \
    for (int i = 0; i < 32; ++i) \
    { \
        if (i >= clampedSamples) \
            break; \
 \
        float travel = (float(i) + 0.5 + (noise - 0.5) * jitterStrength) * stepSize; \
        vec3 samplePosWS = rayOrigin + lightDirWS * travel; \
        vec3 shadowCoord = XRENGINE_ProjectShadowCoord(lightMatrix, samplePosWS); \
        if (!XRENGINE_ShadowCoordInBounds(shadowCoord)) \
            continue; \
 \
        float shadowDepth = XRENGINE_FetchShadowMapTexel(shadowMap, shadowCoord.xy).r; \
        float blockerDelta = shadowCoord.z - shadowDepth; \
        if (blockerDelta <= compareBias) \
            continue; \
 \
        float thickness = max(travel * contactThickness, compareBias * 4.0); \
        if (blockerDelta > thickness) \
            continue; \
 \
        float depthWeight = 1.0 - smoothstep(compareBias, thickness, blockerDelta); \
        float distWeight = 1.0 - (travel / contactDistance); \
        occlusion = max(occlusion, depthWeight * distWeight); \
    } \
 \
    return 1.0 - occlusion * fade;

#define XRENGINE_SHADOW_BODY_SampleContactShadowCube \
    if (contactDistance <= 0.0 || contactSamples <= 0 || farPlaneDist <= 0.0) \
        return 1.0; \
 \
    vec3 lightDirWS = normalize(lightPosWS - fragPosWS); \
    float fade = contactFadeEnd > contactFadeStart \
        ? 1.0 - smoothstep(contactFadeStart, contactFadeEnd, viewDepth) \
        : 1.0; \
    fade *= smoothstep(0.05, 0.35, max(dot(normalize(normalWS), lightDirWS), 0.0)); \
    if (fade <= 0.0) \
        return 1.0; \
 \
    int clampedSamples = clamp(contactSamples, 1, 32); \
    float stepSize = contactDistance / float(clampedSamples); \
    vec3 rayOrigin = fragPosWS + normalWS * max(max(receiverOffset, contactNormalOffset), compareBias * 2.0); \
    float noise = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy); \
 \
    float occlusion = 0.0; \
 \
    for (int i = 0; i < 32; ++i) \
    { \
        if (i >= clampedSamples) \
            break; \
 \
        float travel = (float(i) + 0.5 + (noise - 0.5) * jitterStrength) * stepSize; \
        vec3 samplePosWS = rayOrigin + lightDirWS * travel; \
        vec3 sampleDir = samplePosWS - lightPosWS; \
        float sampleDist = length(sampleDir); \
        if (sampleDist <= 0.0001 || sampleDist >= farPlaneDist) \
            continue; \
 \
        float shadowDepth = XRENGINE_FetchShadowCubeTexel(shadowMap, normalize(sampleDir)).r * farPlaneDist; \
        float blockerDelta = sampleDist - shadowDepth; \
        if (blockerDelta <= compareBias) \
            continue; \
 \
        float thickness = max(travel * contactThickness, compareBias * 4.0); \
        if (blockerDelta > thickness) \
            continue; \
 \
        float depthWeight = 1.0 - smoothstep(compareBias, thickness, blockerDelta); \
        float distWeight = 1.0 - (travel / contactDistance); \
        occlusion = max(occlusion, depthWeight * distWeight); \
    } \
 \
    return 1.0 - occlusion * fade;

#define XRENGINE_SHADOW_BODY_SampleContactShadowScreenSpaceWithNormal \
    if (contactDistance <= 0.0 || contactSamples <= 0) \
        return 1.0; \
 \
    vec3 normal = normalize(normalWS); \
    vec3 lightDir = normalize(lightDirWS); \
    float fade = contactFadeEnd > contactFadeStart \
        ? 1.0 - smoothstep(contactFadeStart, contactFadeEnd, viewDepth) \
        : 1.0; \
    fade *= smoothstep(0.05, 0.35, max(dot(normal, lightDir), 0.0)); \
    if (fade <= 0.0) \
        return 1.0; \
 \
    int clampedSamples = clamp(contactSamples, 1, 32); \
    float stepSize = contactDistance / float(clampedSamples); \
    float normalOffset = max(contactNormalOffset, min(max(receiverOffset, compareBias * 2.0), contactDistance * 0.25)); \
    vec3 rayOriginWS = fragPosWS + normal * normalOffset; \
    float noise = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy); \
    vec2 texelSize = 1.0 / max(screenSize, vec2(1.0)); \
 \
    float occlusion = 0.0; \
    for (int i = 0; i < 32; ++i) \
    { \
        if (i >= clampedSamples) \
            break; \
 \
        float travel = (float(i) + 0.5 + (noise - 0.5) * jitterStrength) * stepSize; \
        travel = clamp(travel, stepSize * 0.25, contactDistance); \
        vec3 samplePosWS = rayOriginWS + lightDir * travel; \
        vec2 sampleUv; \
        if (!XRENGINE_TryProjectContactShadowWorldPos(samplePosWS, viewProjectionMatrix, sampleUv)) \
            continue; \
 \
        vec2 sampleUvClamped = clamp(sampleUv, texelSize * 0.5, 1.0 - texelSize * 0.5); \
        float sceneDepthValue = XRENGINE_FetchContactShadowDepth(sceneDepth, sampleUvClamped, layer).r; \
        vec3 sceneNormalWS = XRENGINE_DecodeContactShadowNormal(XRENGINE_FetchContactShadowNormal(sceneNormal, sampleUvClamped, layer).rg); \
        occlusion = max(occlusion, XRENGINE_EvaluateContactShadowScreenSpaceHit( \
            sceneDepthValue, \
            sampleUv, \
            rayOriginWS, \
            lightDir, \
            normal, \
            sceneNormalWS, \
            true, \
            samplePosWS, \
            travel, \
            stepSize, \
            contactDistance, \
            contactThickness, \
            compareBias, \
            viewMatrix, \
            inverseProjMatrix, \
            inverseViewMatrix, \
            depthMode)); \
    } \
 \
    return 1.0 - occlusion * fade;
