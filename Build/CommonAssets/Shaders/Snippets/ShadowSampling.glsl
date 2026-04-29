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

const float XRENGINE_ShadowPi = 3.14159265359;

float XRENGINE_InterleavedGradientNoise(vec2 pixel)
{
    return fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
}

vec2 XRENGINE_RotateDiskTap(vec2 tap, float rotation)
{
    float s = sin(rotation);
    float c = cos(rotation);
    return vec2(c * tap.x - s * tap.y, s * tap.x + c * tap.y);
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

float XRENGINE_ResolveContactShadowDepth(float depth, int depthMode)
{
    return depthMode == 1 ? (1.0 - depth) : depth;
}

vec3 XRENGINE_ContactShadowWorldPosFromDepth(
    float depth,
    vec2 uv,
    mat4 inverseProjMatrix,
    mat4 inverseViewMatrix,
    int depthMode)
{
    float resolvedDepth = XRENGINE_ResolveContactShadowDepth(depth, depthMode);
    vec4 clipSpacePosition = vec4(uv * 2.0 - 1.0, resolvedDepth * 2.0 - 1.0, 1.0);
    vec4 viewSpacePosition = inverseProjMatrix * clipSpacePosition;
    viewSpacePosition /= viewSpacePosition.w;
    return (inverseViewMatrix * viewSpacePosition).xyz;
}

float XRENGINE_ContactShadowScreenEdgeFade(vec2 uv)
{
    vec2 edge = min(uv, 1.0 - uv);
    return smoothstep(0.0, 0.025, min(edge.x, edge.y));
}

vec3 XRENGINE_DecodeContactShadowNormal(vec2 encoded)
{
    vec2 f = encoded * 2.0 - 1.0;
    vec3 n = vec3(f, 1.0 - abs(f.x) - abs(f.y));
    float t = clamp(-n.z, 0.0, 1.0);
    n.xy -= vec2(n.x >= 0.0 ? t : -t, n.y >= 0.0 ? t : -t);
    return normalize(n);
}

float XRENGINE_EvaluateContactShadowScreenSpaceHit(
    float sceneDepth,
    vec2 sampleUv,
    vec3 rayOriginWS,
    vec3 lightDirWS,
    vec3 receiverNormalWS,
    vec3 sceneNormalWS,
    bool useSceneNormal,
    vec3 samplePosWS,
    float travel,
    float stepSize,
    float contactDistance,
    float contactThickness,
    float compareBias,
    mat4 viewMatrix,
    mat4 inverseProjMatrix,
    mat4 inverseViewMatrix,
    int depthMode)
{
    float resolvedSceneDepth = XRENGINE_ResolveContactShadowDepth(sceneDepth, depthMode);
    if (resolvedSceneDepth >= 0.999999)
        return 0.0;

    vec3 scenePosWS = XRENGINE_ContactShadowWorldPosFromDepth(
        sceneDepth,
        sampleUv,
        inverseProjMatrix,
        inverseViewMatrix,
        depthMode);

    float sampleViewDepth = abs((viewMatrix * vec4(samplePosWS, 1.0)).z);
    float sceneViewDepth = abs((viewMatrix * vec4(scenePosWS, 1.0)).z);
    float depthDelta = sampleViewDepth - sceneViewDepth;

    float contactBias = max(min(compareBias, contactDistance * 0.05), contactDistance * 0.001);
    if (depthDelta <= contactBias)
        return 0.0;

    vec3 toScene = scenePosWS - rayOriginWS;
    float alongRay = dot(toScene, lightDirWS);
    if (alongRay <= contactBias || alongRay > contactDistance || alongRay > travel + stepSize)
        return 0.0;

    vec3 closestPointWS = rayOriginWS + lightDirWS * alongRay;
    float rayDistance = length(scenePosWS - closestPointWS);
    float thickness = max(max(alongRay * contactThickness, contactDistance * 0.01), contactBias * 2.0);
    if (rayDistance > thickness)
        return 0.0;

    float rayWeight = 1.0 - smoothstep(thickness * 0.35, thickness, rayDistance);
    float distanceWeight = 1.0 - clamp(alongRay / contactDistance, 0.0, 1.0);
    float normalWeight = 1.0;
    if (useSceneNormal)
    {
        float sameSurfaceNormal = smoothstep(0.92, 0.995, dot(normalize(receiverNormalWS), normalize(sceneNormalWS)));
        float shallowHit = 1.0 - smoothstep(contactBias * 2.0, max(thickness, contactBias * 3.0), depthDelta);
        normalWeight = mix(1.0, 0.35, sameSurfaceNormal * shallowHit);
    }

    return rayWeight * distanceWeight * normalWeight * XRENGINE_ContactShadowScreenEdgeFade(sampleUv);
}

float XRENGINE_SampleContactShadowScreenSpace(
    sampler2D sceneDepth,
    vec2 screenSize,
    mat4 viewMatrix,
    mat4 inverseProjMatrix,
    mat4 inverseViewMatrix,
    mat4 viewProjectionMatrix,
    int depthMode,
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
    if (contactDistance <= 0.0 || contactSamples <= 0)
        return 1.0;

    vec3 normal = normalize(normalWS);
    vec3 lightDir = normalize(lightDirWS);
    float fade = contactFadeEnd > contactFadeStart
        ? 1.0 - smoothstep(contactFadeStart, contactFadeEnd, viewDepth)
        : 1.0;
    fade *= smoothstep(0.05, 0.35, max(dot(normal, lightDir), 0.0));
    if (fade <= 0.0)
        return 1.0;

    int clampedSamples = clamp(contactSamples, 1, 32);
    float stepSize = contactDistance / float(clampedSamples);
    float normalOffset = max(contactNormalOffset, min(max(receiverOffset, compareBias * 2.0), contactDistance * 0.25));
    vec3 rayOriginWS = fragPosWS + normal * normalOffset;
    float noise = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy);
    vec2 texelSize = 1.0 / max(screenSize, vec2(1.0));

    float occlusion = 0.0;
    for (int i = 0; i < 32; ++i)
    {
        if (i >= clampedSamples)
            break;

        float travel = (float(i) + 0.5 + (noise - 0.5) * jitterStrength) * stepSize;
        travel = clamp(travel, stepSize * 0.25, contactDistance);
        vec3 samplePosWS = rayOriginWS + lightDir * travel;
        vec4 sampleClip = viewProjectionMatrix * vec4(samplePosWS, 1.0);
        if (sampleClip.w <= 0.0001)
            continue;

        vec3 sampleNdc = sampleClip.xyz / sampleClip.w;
        vec2 sampleUv = sampleNdc.xy * 0.5 + 0.5;
        if (sampleUv.x <= 0.0 || sampleUv.x >= 1.0 || sampleUv.y <= 0.0 || sampleUv.y >= 1.0)
            continue;

        float sceneDepthValue = texture(sceneDepth, clamp(sampleUv, texelSize * 0.5, 1.0 - texelSize * 0.5)).r;
        occlusion = max(occlusion, XRENGINE_EvaluateContactShadowScreenSpaceHit(
            sceneDepthValue,
            sampleUv,
            rayOriginWS,
            lightDir,
            normal,
            vec3(0.0),
            false,
            samplePosWS,
            travel,
            stepSize,
            contactDistance,
            contactThickness,
            compareBias,
            viewMatrix,
            inverseProjMatrix,
            inverseViewMatrix,
            depthMode));
    }

    return 1.0 - occlusion * fade;
}

float XRENGINE_SampleContactShadowScreenSpace(
    sampler2D sceneDepth,
    sampler2D sceneNormal,
    vec2 screenSize,
    mat4 viewMatrix,
    mat4 inverseProjMatrix,
    mat4 inverseViewMatrix,
    mat4 viewProjectionMatrix,
    int depthMode,
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
    if (contactDistance <= 0.0 || contactSamples <= 0)
        return 1.0;

    vec3 normal = normalize(normalWS);
    vec3 lightDir = normalize(lightDirWS);
    float fade = contactFadeEnd > contactFadeStart
        ? 1.0 - smoothstep(contactFadeStart, contactFadeEnd, viewDepth)
        : 1.0;
    fade *= smoothstep(0.05, 0.35, max(dot(normal, lightDir), 0.0));
    if (fade <= 0.0)
        return 1.0;

    int clampedSamples = clamp(contactSamples, 1, 32);
    float stepSize = contactDistance / float(clampedSamples);
    float normalOffset = max(contactNormalOffset, min(max(receiverOffset, compareBias * 2.0), contactDistance * 0.25));
    vec3 rayOriginWS = fragPosWS + normal * normalOffset;
    float noise = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy);
    vec2 texelSize = 1.0 / max(screenSize, vec2(1.0));

    float occlusion = 0.0;
    for (int i = 0; i < 32; ++i)
    {
        if (i >= clampedSamples)
            break;

        float travel = (float(i) + 0.5 + (noise - 0.5) * jitterStrength) * stepSize;
        travel = clamp(travel, stepSize * 0.25, contactDistance);
        vec3 samplePosWS = rayOriginWS + lightDir * travel;
        vec4 sampleClip = viewProjectionMatrix * vec4(samplePosWS, 1.0);
        if (sampleClip.w <= 0.0001)
            continue;

        vec3 sampleNdc = sampleClip.xyz / sampleClip.w;
        vec2 sampleUv = sampleNdc.xy * 0.5 + 0.5;
        if (sampleUv.x <= 0.0 || sampleUv.x >= 1.0 || sampleUv.y <= 0.0 || sampleUv.y >= 1.0)
            continue;

        vec2 sampleUvClamped = clamp(sampleUv, texelSize * 0.5, 1.0 - texelSize * 0.5);
        float sceneDepthValue = texture(sceneDepth, sampleUvClamped).r;
        vec3 sceneNormalWS = XRENGINE_DecodeContactShadowNormal(texture(sceneNormal, sampleUvClamped).rg);
        occlusion = max(occlusion, XRENGINE_EvaluateContactShadowScreenSpaceHit(
            sceneDepthValue,
            sampleUv,
            rayOriginWS,
            lightDir,
            normal,
            sceneNormalWS,
            true,
            samplePosWS,
            travel,
            stepSize,
            contactDistance,
            contactThickness,
            compareBias,
            viewMatrix,
            inverseProjMatrix,
            inverseViewMatrix,
            depthMode));
    }

    return 1.0 - occlusion * fade;
}

float XRENGINE_SampleContactShadowScreenSpace(
    sampler2DArray sceneDepth,
    sampler2DArray sceneNormal,
    float layer,
    vec2 screenSize,
    mat4 viewMatrix,
    mat4 inverseProjMatrix,
    mat4 inverseViewMatrix,
    mat4 viewProjectionMatrix,
    int depthMode,
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
    if (contactDistance <= 0.0 || contactSamples <= 0)
        return 1.0;

    vec3 normal = normalize(normalWS);
    vec3 lightDir = normalize(lightDirWS);
    float fade = contactFadeEnd > contactFadeStart
        ? 1.0 - smoothstep(contactFadeStart, contactFadeEnd, viewDepth)
        : 1.0;
    fade *= smoothstep(0.05, 0.35, max(dot(normal, lightDir), 0.0));
    if (fade <= 0.0)
        return 1.0;

    int clampedSamples = clamp(contactSamples, 1, 32);
    float stepSize = contactDistance / float(clampedSamples);
    float normalOffset = max(contactNormalOffset, min(max(receiverOffset, compareBias * 2.0), contactDistance * 0.25));
    vec3 rayOriginWS = fragPosWS + normal * normalOffset;
    float noise = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy);
    vec2 texelSize = 1.0 / max(screenSize, vec2(1.0));

    float occlusion = 0.0;
    for (int i = 0; i < 32; ++i)
    {
        if (i >= clampedSamples)
            break;

        float travel = (float(i) + 0.5 + (noise - 0.5) * jitterStrength) * stepSize;
        travel = clamp(travel, stepSize * 0.25, contactDistance);
        vec3 samplePosWS = rayOriginWS + lightDir * travel;
        vec4 sampleClip = viewProjectionMatrix * vec4(samplePosWS, 1.0);
        if (sampleClip.w <= 0.0001)
            continue;

        vec3 sampleNdc = sampleClip.xyz / sampleClip.w;
        vec2 sampleUv = sampleNdc.xy * 0.5 + 0.5;
        if (sampleUv.x <= 0.0 || sampleUv.x >= 1.0 || sampleUv.y <= 0.0 || sampleUv.y >= 1.0)
            continue;

        vec2 sampleUvClamped = clamp(sampleUv, texelSize * 0.5, 1.0 - texelSize * 0.5);
        vec3 sampleCoord = vec3(sampleUvClamped, layer);
        float sceneDepthValue = texture(sceneDepth, sampleCoord).r;
        vec3 sceneNormalWS = XRENGINE_DecodeContactShadowNormal(texture(sceneNormal, sampleCoord).rg);
        occlusion = max(occlusion, XRENGINE_EvaluateContactShadowScreenSpaceHit(
            sceneDepthValue,
            sampleUv,
            rayOriginWS,
            lightDir,
            normal,
            sceneNormalWS,
            true,
            samplePosWS,
            travel,
            stepSize,
            contactDistance,
            contactThickness,
            compareBias,
            viewMatrix,
            inverseProjMatrix,
            inverseViewMatrix,
            depthMode));
    }

    return 1.0 - occlusion * fade;
}

float XRENGINE_SampleContactShadowScreenSpace(
    sampler2DMS sceneDepth,
    int sampleId,
    vec2 screenSize,
    mat4 viewMatrix,
    mat4 inverseProjMatrix,
    mat4 inverseViewMatrix,
    mat4 viewProjectionMatrix,
    int depthMode,
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
    if (contactDistance <= 0.0 || contactSamples <= 0)
        return 1.0;

    vec3 normal = normalize(normalWS);
    vec3 lightDir = normalize(lightDirWS);
    float fade = contactFadeEnd > contactFadeStart
        ? 1.0 - smoothstep(contactFadeStart, contactFadeEnd, viewDepth)
        : 1.0;
    fade *= smoothstep(0.05, 0.35, max(dot(normal, lightDir), 0.0));
    if (fade <= 0.0)
        return 1.0;

    int clampedSamples = clamp(contactSamples, 1, 32);
    float stepSize = contactDistance / float(clampedSamples);
    float normalOffset = max(contactNormalOffset, min(max(receiverOffset, compareBias * 2.0), contactDistance * 0.25));
    vec3 rayOriginWS = fragPosWS + normal * normalOffset;
    float noise = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy);
    ivec2 depthSize = textureSize(sceneDepth);
    vec2 safeScreenSize = max(screenSize, vec2(1.0));

    float occlusion = 0.0;
    for (int i = 0; i < 32; ++i)
    {
        if (i >= clampedSamples)
            break;

        float travel = (float(i) + 0.5 + (noise - 0.5) * jitterStrength) * stepSize;
        travel = clamp(travel, stepSize * 0.25, contactDistance);
        vec3 samplePosWS = rayOriginWS + lightDir * travel;
        vec4 sampleClip = viewProjectionMatrix * vec4(samplePosWS, 1.0);
        if (sampleClip.w <= 0.0001)
            continue;

        vec3 sampleNdc = sampleClip.xyz / sampleClip.w;
        vec2 sampleUv = sampleNdc.xy * 0.5 + 0.5;
        if (sampleUv.x <= 0.0 || sampleUv.x >= 1.0 || sampleUv.y <= 0.0 || sampleUv.y >= 1.0)
            continue;

        ivec2 sampleCoord = ivec2(clamp(floor(sampleUv * safeScreenSize), vec2(0.0), vec2(depthSize - ivec2(1))));
        float sceneDepthValue = texelFetch(sceneDepth, sampleCoord, sampleId).r;
        occlusion = max(occlusion, XRENGINE_EvaluateContactShadowScreenSpaceHit(
            sceneDepthValue,
            sampleUv,
            rayOriginWS,
            lightDir,
            normal,
            vec3(0.0),
            false,
            samplePosWS,
            travel,
            stepSize,
            contactDistance,
            contactThickness,
            compareBias,
            viewMatrix,
            inverseProjMatrix,
            inverseViewMatrix,
            depthMode));
    }

    return 1.0 - occlusion * fade;
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

const int XRENGINE_MaxVogelShadowTaps = 32;
const float XRENGINE_VogelGoldenAngle = 2.39996323;

vec2 XRENGINE_GetVogelDiskTap(int tapIndex, int tapCount)
{
    float sampleIndex = float(tapIndex) + 0.5;
    float radius = sqrt(sampleIndex / float(max(tapCount, 1)));
    float angle = sampleIndex * XRENGINE_VogelGoldenAngle;
    return radius * vec2(cos(angle), sin(angle));
}

vec2 XRENGINE_GetVogelDiskTapRotated(int tapIndex, int tapCount, float rotation)
{
    return XRENGINE_RotateDiskTap(XRENGINE_GetVogelDiskTap(tapIndex, tapCount), rotation);
}

void XRENGINE_BuildOrthonormalBasis(vec3 normal, out vec3 tangent, out vec3 bitangent)
{
    vec3 up = abs(normal.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
    tangent = normalize(cross(up, normal));
    bitangent = cross(normal, tangent);
}

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

float XRENGINE_SampleShadowMapVogel(sampler2D shadowMap, vec3 shadowCoord, float bias, int tapCount, float filterRadius)
{
    int clampedTaps = clamp(tapCount, 1, XRENGINE_MaxVogelShadowTaps);
    if (clampedTaps <= 1)
    {
        float depth = texture(shadowMap, shadowCoord.xy).r;
        return (shadowCoord.z - bias) > depth ? 0.0 : 1.0;
    }

    vec2 texelSize = 1.0 / vec2(textureSize(shadowMap, 0));
    float radius = max(filterRadius, max(texelSize.x, texelSize.y));
    float lit = 0.0;
    float rotation = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy) * XRENGINE_ShadowPi * 2.0;

    for (int i = 0; i < XRENGINE_MaxVogelShadowTaps; ++i)
    {
        if (i >= clampedTaps)
            break;

        vec2 sampleUv = shadowCoord.xy + XRENGINE_GetVogelDiskTapRotated(i, clampedTaps, rotation) * radius;
        float sampleDepth = texture(shadowMap, sampleUv).r;
        lit += (shadowCoord.z - bias) <= sampleDepth ? 1.0 : 0.0;
    }

    return lit / float(clampedTaps);
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

float XRENGINE_SampleShadowMapArrayVogel(sampler2DArray shadowMap, vec3 shadowCoord, float layer, float bias, int tapCount, float filterRadius)
{
    int clampedTaps = clamp(tapCount, 1, XRENGINE_MaxVogelShadowTaps);
    if (clampedTaps <= 1)
        return XRENGINE_SampleShadowMapArraySimple(shadowMap, shadowCoord, layer, bias);

    vec2 texelSize = 1.0 / vec2(textureSize(shadowMap, 0).xy);
    float radius = max(filterRadius, max(texelSize.x, texelSize.y));
    float lit = 0.0;
    float rotation = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy) * XRENGINE_ShadowPi * 2.0;

    for (int i = 0; i < XRENGINE_MaxVogelShadowTaps; ++i)
    {
        if (i >= clampedTaps)
            break;

        vec2 sampleUv = shadowCoord.xy + XRENGINE_GetVogelDiskTapRotated(i, clampedTaps, rotation) * radius;
        float sampleDepth = texture(shadowMap, vec3(sampleUv, layer)).r;
        lit += (shadowCoord.z - bias) <= sampleDepth ? 1.0 : 0.0;
    }

    return lit / float(clampedTaps);
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
    int contactSamples,
    float contactThickness,
    float contactFadeStart,
    float contactFadeEnd,
    float contactNormalOffset,
    float jitterStrength,
    float viewDepth)
{
    if (contactDistance <= 0.0 || contactSamples <= 0)
        return 1.0;

    float fade = contactFadeEnd > contactFadeStart
        ? 1.0 - smoothstep(contactFadeStart, contactFadeEnd, viewDepth)
        : 1.0;
    fade *= smoothstep(0.05, 0.35, max(dot(normalize(normalWS), normalize(lightDirWS)), 0.0));
    if (fade <= 0.0)
        return 1.0;

    int clampedSamples = clamp(contactSamples, 1, 32);
    float stepSize = contactDistance / float(clampedSamples);
    vec3 rayOrigin = fragPosWS + normalWS * max(max(receiverOffset, contactNormalOffset), compareBias * 2.0);

    // Interleaved gradient noise (Jimenez 2014): stable per-pixel dithering.
    float noise = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy);

    float occlusion = 0.0;

    for (int i = 0; i < 32; ++i)
    {
        if (i >= clampedSamples)
            break;

        // Dithered step: center of interval plus noise offset.
        float travel = (float(i) + 0.5 + (noise - 0.5) * jitterStrength) * stepSize;
        vec3 samplePosWS = rayOrigin + lightDirWS * travel;
        vec3 shadowCoord = XRENGINE_ProjectShadowCoord(lightMatrix, samplePosWS);
        if (!XRENGINE_ShadowCoordInBounds(shadowCoord))
            continue;

        float shadowDepth = texture(shadowMap, shadowCoord.xy).r;
        float blockerDelta = shadowCoord.z - shadowDepth;
        if (blockerDelta <= compareBias)
            continue;

        // Proportional thickness: stable regardless of shadow map resolution.
        float thickness = max(travel * contactThickness, compareBias * 4.0);
        if (blockerDelta > thickness)
            continue;

        float depthWeight = 1.0 - smoothstep(compareBias, thickness, blockerDelta);
        float distWeight = 1.0 - (travel / contactDistance);
        occlusion = max(occlusion, depthWeight * distWeight);
    }

    return 1.0 - occlusion * fade;
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
    return XRENGINE_SampleContactShadow2D(
        shadowMap,
        lightMatrix,
        fragPosWS,
        normalWS,
        lightDirWS,
        receiverOffset,
        compareBias,
        contactDistance,
        contactSamples,
        0.25,
        0.0,
        1000000.0,
        0.0,
        1.0,
        0.0);
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
    int contactSamples,
    float contactThickness,
    float contactFadeStart,
    float contactFadeEnd,
    float contactNormalOffset,
    float jitterStrength,
    float viewDepth)
{
    if (contactDistance <= 0.0 || contactSamples <= 0)
        return 1.0;

    float fade = contactFadeEnd > contactFadeStart
        ? 1.0 - smoothstep(contactFadeStart, contactFadeEnd, viewDepth)
        : 1.0;
    fade *= smoothstep(0.05, 0.35, max(dot(normalize(normalWS), normalize(lightDirWS)), 0.0));
    if (fade <= 0.0)
        return 1.0;

    int clampedSamples = clamp(contactSamples, 1, 32);
    float stepSize = contactDistance / float(clampedSamples);
    vec3 rayOrigin = fragPosWS + normalWS * max(max(receiverOffset, contactNormalOffset), compareBias * 2.0);

    // Interleaved gradient noise (Jimenez 2014): stable per-pixel dithering.
    float noise = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy);

    float occlusion = 0.0;

    for (int i = 0; i < 32; ++i)
    {
        if (i >= clampedSamples)
            break;

        float travel = (float(i) + 0.5 + (noise - 0.5) * jitterStrength) * stepSize;
        vec3 samplePosWS = rayOrigin + lightDirWS * travel;
        vec3 shadowCoord = XRENGINE_ProjectShadowCoord(lightMatrix, samplePosWS);
        if (!XRENGINE_ShadowCoordInBounds(shadowCoord))
            continue;

        float shadowDepth = texture(shadowMap, vec3(shadowCoord.xy, layer)).r;
        float blockerDelta = shadowCoord.z - shadowDepth;
        if (blockerDelta <= compareBias)
            continue;

        float thickness = max(travel * contactThickness, compareBias * 4.0);
        if (blockerDelta > thickness)
            continue;

        float depthWeight = 1.0 - smoothstep(compareBias, thickness, blockerDelta);
        float distWeight = 1.0 - (travel / contactDistance);
        occlusion = max(occlusion, depthWeight * distWeight);
    }

    return 1.0 - occlusion * fade;
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
    return XRENGINE_SampleContactShadowArray(
        shadowMap,
        lightMatrix,
        layer,
        fragPosWS,
        normalWS,
        lightDirWS,
        receiverOffset,
        compareBias,
        contactDistance,
        contactSamples,
        0.25,
        0.0,
        1000000.0,
        0.0,
        1.0,
        0.0);
}

float XRENGINE_SampleContactShadowCube(
    samplerCube shadowMap,
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
    if (contactDistance <= 0.0 || contactSamples <= 0 || farPlaneDist <= 0.0)
        return 1.0;

    vec3 lightDirWS = normalize(lightPosWS - fragPosWS);
    float fade = contactFadeEnd > contactFadeStart
        ? 1.0 - smoothstep(contactFadeStart, contactFadeEnd, viewDepth)
        : 1.0;
    fade *= smoothstep(0.05, 0.35, max(dot(normalize(normalWS), lightDirWS), 0.0));
    if (fade <= 0.0)
        return 1.0;

    int clampedSamples = clamp(contactSamples, 1, 32);
    float stepSize = contactDistance / float(clampedSamples);
    vec3 rayOrigin = fragPosWS + normalWS * max(max(receiverOffset, contactNormalOffset), compareBias * 2.0);
    float noise = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy);

    float occlusion = 0.0;

    for (int i = 0; i < 32; ++i)
    {
        if (i >= clampedSamples)
            break;

        float travel = (float(i) + 0.5 + (noise - 0.5) * jitterStrength) * stepSize;
        vec3 samplePosWS = rayOrigin + lightDirWS * travel;
        vec3 sampleDir = samplePosWS - lightPosWS;
        float sampleDist = length(sampleDir);
        if (sampleDist <= 0.0001 || sampleDist >= farPlaneDist)
            continue;

        float shadowDepth = texture(shadowMap, normalize(sampleDir)).r * farPlaneDist;
        float blockerDelta = sampleDist - shadowDepth;
        if (blockerDelta <= compareBias)
            continue;

        float thickness = max(travel * contactThickness, compareBias * 4.0);
        if (blockerDelta > thickness)
            continue;

        float depthWeight = 1.0 - smoothstep(compareBias, thickness, blockerDelta);
        float distWeight = 1.0 - (travel / contactDistance);
        occlusion = max(occlusion, depthWeight * distWeight);
    }

    return 1.0 - occlusion * fade;
}

float XRENGINE_SampleContactShadowCube(
    samplerCube shadowMap,
    vec3 fragPosWS,
    vec3 normalWS,
    vec3 lightPosWS,
    float receiverOffset,
    float compareBias,
    float farPlaneDist,
    float contactDistance,
    int contactSamples)
{
    return XRENGINE_SampleContactShadowCube(
        shadowMap,
        fragPosWS,
        normalWS,
        lightPosWS,
        receiverOffset,
        compareBias,
        farPlaneDist,
        contactDistance,
        contactSamples,
        0.25,
        0.0,
        1000000.0,
        0.0,
        1.0,
        0.0);
}

// --- Cube shadow kernel and helpers (must precede CHSS cube functions) ---

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

float XRENGINE_SampleShadowCubeVogel(samplerCube shadowMap, vec3 shadowDir, float compareDepth, float bias, float farPlaneDist, float sampleRadius, int tapCount)
{
    int clampedTaps = clamp(tapCount, 1, XRENGINE_MaxVogelShadowTaps);
    if (clampedTaps <= 1)
        return XRENGINE_SampleShadowCubeSimple(shadowMap, shadowDir, compareDepth, bias, farPlaneDist);

    vec3 baseDir = normalize(shadowDir);
    vec3 tangent;
    vec3 bitangent;
    XRENGINE_BuildOrthonormalBasis(baseDir, tangent, bitangent);
    float lit = 0.0;
    float rotation = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy) * XRENGINE_ShadowPi * 2.0;

    for (int i = 0; i < XRENGINE_MaxVogelShadowTaps; ++i)
    {
        if (i >= clampedTaps)
            break;

        vec2 diskTap = XRENGINE_GetVogelDiskTapRotated(i, clampedTaps, rotation) * sampleRadius;
        vec3 sampleDir = normalize(baseDir + tangent * diskTap.x + bitangent * diskTap.y);
        float sampleDepth = texture(shadowMap, sampleDir).r * farPlaneDist;
        lit += (compareDepth - bias) <= sampleDepth ? 1.0 : 0.0;
    }

    return lit / float(clampedTaps);
}

// --- Contact-Hardening Soft Shadows (PCSS/CHSS) ---
// Two-pass technique: a blocker search estimates average occluder depth, then
// the penumbra width is derived from the receiver-to-blocker ratio and source size.

float XRENGINE_ClampPenumbra(float penumbra, float minRadius, float maxRadius)
{
    float safeMin = max(minRadius, 0.0);
    float safeMax = max(maxRadius, safeMin);
    return clamp(penumbra, safeMin, safeMax);
}

float XRENGINE_EstimatePenumbra(float receiverDepth, float avgBlockerDepth, float lightSourceRadius, float minRadius, float maxRadius)
{
    float penumbra = (receiverDepth - avgBlockerDepth) / max(avgBlockerDepth, 0.0001) * max(lightSourceRadius, 0.0);
    return XRENGINE_ClampPenumbra(penumbra, minRadius, maxRadius);
}

// Blocker search for 2D shadow maps. Returns average blocker depth, or -1.0 if no blockers were found.
float XRENGINE_BlockerSearch2D(sampler2D shadowMap, vec2 uv, float receiverDepth, float searchRadius, int sampleCount)
{
    float blockerSum = 0.0;
    int blockerCount = 0;
    int clampedSamples = clamp(sampleCount, 1, XRENGINE_MaxVogelShadowTaps);
    vec2 texelSize = 1.0 / vec2(textureSize(shadowMap, 0));
    float radius = max(searchRadius, max(texelSize.x, texelSize.y));
    float rotation = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy) * XRENGINE_ShadowPi * 2.0;

    for (int i = 0; i < XRENGINE_MaxVogelShadowTaps; ++i)
    {
        if (i >= clampedSamples)
            break;

        vec2 sampleUv = uv + XRENGINE_GetVogelDiskTapRotated(i, clampedSamples, rotation) * radius;
        float sampleDepth = texture(shadowMap, sampleUv).r;
        if (sampleDepth < receiverDepth)
        {
            blockerSum += sampleDepth;
            blockerCount++;
        }
    }

    return blockerCount > 0 ? blockerSum / float(blockerCount) : -1.0;
}

// Blocker search for 2D array shadow maps.
float XRENGINE_BlockerSearch2DArray(sampler2DArray shadowMap, vec2 uv, float layer, float receiverDepth, float searchRadius, int sampleCount)
{
    float blockerSum = 0.0;
    int blockerCount = 0;
    int clampedSamples = clamp(sampleCount, 1, XRENGINE_MaxVogelShadowTaps);
    vec2 texelSize = 1.0 / vec2(textureSize(shadowMap, 0).xy);
    float radius = max(searchRadius, max(texelSize.x, texelSize.y));
    float rotation = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy) * XRENGINE_ShadowPi * 2.0;

    for (int i = 0; i < XRENGINE_MaxVogelShadowTaps; ++i)
    {
        if (i >= clampedSamples)
            break;

        vec2 sampleUv = uv + XRENGINE_GetVogelDiskTapRotated(i, clampedSamples, rotation) * radius;
        float sampleDepth = texture(shadowMap, vec3(sampleUv, layer)).r;
        if (sampleDepth < receiverDepth)
        {
            blockerSum += sampleDepth;
            blockerCount++;
        }
    }

    return blockerCount > 0 ? blockerSum / float(blockerCount) : -1.0;
}

// Blocker search for cubemap shadow maps.
float XRENGINE_BlockerSearchCube(samplerCube shadowMap, vec3 shadowDir, float compareDepth, float farPlaneDist, float sampleRadius, int sampleCount)
{
    float blockerSum = 0.0;
    int blockerCount = 0;
    int clampedSamples = clamp(sampleCount, 1, XRENGINE_MaxVogelShadowTaps);
    vec3 baseDir = normalize(shadowDir);
    vec3 tangent;
    vec3 bitangent;
    XRENGINE_BuildOrthonormalBasis(baseDir, tangent, bitangent);
    float radius = max(sampleRadius, 0.000001);
    float rotation = XRENGINE_InterleavedGradientNoise(gl_FragCoord.xy) * XRENGINE_ShadowPi * 2.0;

    for (int i = 0; i < XRENGINE_MaxVogelShadowTaps; ++i)
    {
        if (i >= clampedSamples)
            break;

        vec2 diskTap = XRENGINE_GetVogelDiskTapRotated(i, clampedSamples, rotation) * radius;
        vec3 sampleDir = normalize(baseDir + tangent * diskTap.x + bitangent * diskTap.y);
        float sampleDepth = texture(shadowMap, sampleDir).r * farPlaneDist;
        if (sampleDepth < compareDepth)
        {
            blockerSum += sampleDepth;
            blockerCount++;
        }
    }

    return blockerCount > 0 ? blockerSum / float(blockerCount) : -1.0;
}

// PCSS/CHSS for 2D shadow maps: blocker search -> variable penumbra -> Vogel filtering.
float XRENGINE_SampleShadowMapCHSS(
    sampler2D shadowMap,
    vec3 shadowCoord,
    float bias,
    int blockerSamples,
    int filterSamples,
    float searchRadius,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra)
{
    float receiverDepth = shadowCoord.z - bias;
    float avgBlockerDepth = XRENGINE_BlockerSearch2D(shadowMap, shadowCoord.xy, receiverDepth, searchRadius, blockerSamples);

    if (avgBlockerDepth < 0.0)
        return 1.0;

    vec2 texelSize = 1.0 / vec2(textureSize(shadowMap, 0));
    float minRadius = max(minPenumbra, max(texelSize.x, texelSize.y));
    float penumbra = XRENGINE_EstimatePenumbra(receiverDepth, avgBlockerDepth, lightSourceRadius, minRadius, max(maxPenumbra, minRadius));

    return XRENGINE_SampleShadowMapVogel(shadowMap, shadowCoord, bias, filterSamples, penumbra);
}

float XRENGINE_SampleShadowMapCHSS(sampler2D shadowMap, vec3 shadowCoord, float bias, int sampleCount, float searchRadius, float lightSourceRadius)
{
    return XRENGINE_SampleShadowMapCHSS(
        shadowMap,
        shadowCoord,
        bias,
        sampleCount,
        sampleCount,
        searchRadius,
        lightSourceRadius,
        0.0,
        searchRadius * 4.0);
}

// PCSS/CHSS for 2D array shadow maps (cascaded directional shadows).
float XRENGINE_SampleShadowMapArrayCHSS(
    sampler2DArray shadowMap,
    vec3 shadowCoord,
    float layer,
    float bias,
    int blockerSamples,
    int filterSamples,
    float searchRadius,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra)
{
    float receiverDepth = shadowCoord.z - bias;
    float avgBlockerDepth = XRENGINE_BlockerSearch2DArray(shadowMap, shadowCoord.xy, layer, receiverDepth, searchRadius, blockerSamples);

    if (avgBlockerDepth < 0.0)
        return 1.0;

    vec2 texelSize = 1.0 / vec2(textureSize(shadowMap, 0).xy);
    float minRadius = max(minPenumbra, max(texelSize.x, texelSize.y));
    float penumbra = XRENGINE_EstimatePenumbra(receiverDepth, avgBlockerDepth, lightSourceRadius, minRadius, max(maxPenumbra, minRadius));

    return XRENGINE_SampleShadowMapArrayVogel(shadowMap, shadowCoord, layer, bias, filterSamples, penumbra);
}

float XRENGINE_SampleShadowMapArrayCHSS(sampler2DArray shadowMap, vec3 shadowCoord, float layer, float bias, int sampleCount, float searchRadius, float lightSourceRadius)
{
    return XRENGINE_SampleShadowMapArrayCHSS(
        shadowMap,
        shadowCoord,
        layer,
        bias,
        sampleCount,
        sampleCount,
        searchRadius,
        lightSourceRadius,
        0.0,
        searchRadius * 4.0);
}

// PCSS/CHSS for cubemap shadow maps (point lights).
float XRENGINE_SampleShadowCubeCHSS(
    samplerCube shadowMap,
    vec3 shadowDir,
    float compareDepth,
    float bias,
    float farPlaneDist,
    float blockerSearchRadius,
    int blockerSamples,
    int filterSamples,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra)
{
    float receiverDepth = compareDepth - bias;
    float avgBlockerDepth = XRENGINE_BlockerSearchCube(shadowMap, shadowDir, receiverDepth, farPlaneDist, blockerSearchRadius, blockerSamples);

    if (avgBlockerDepth < 0.0)
        return 1.0;

    float angularSourceRadius = max(lightSourceRadius, 0.0) / max(avgBlockerDepth, 0.0001);
    float rawPenumbra = (receiverDepth - avgBlockerDepth) / max(avgBlockerDepth, 0.0001) * angularSourceRadius;
    float penumbra = XRENGINE_ClampPenumbra(rawPenumbra, max(minPenumbra, 0.000001), max(maxPenumbra, minPenumbra));

    return XRENGINE_SampleShadowCubeVogel(shadowMap, shadowDir, compareDepth, bias, farPlaneDist, penumbra, filterSamples);
}

float XRENGINE_SampleShadowCubeCHSS(samplerCube shadowMap, vec3 shadowDir, float compareDepth, float bias, float farPlaneDist, float sampleRadius, int sampleCount, float lightSourceRadius)
{
    return XRENGINE_SampleShadowCubeCHSS(
        shadowMap,
        shadowDir,
        compareDepth,
        bias,
        farPlaneDist,
        sampleRadius,
        sampleCount,
        sampleCount,
        lightSourceRadius,
        sampleRadius * 0.1,
        sampleRadius * 4.0);
}

// --- End PCSS/CHSS ---

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

float XRENGINE_SampleShadowMapFiltered(
    sampler2D shadowMap,
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
    if (softShadowMode == 3) // VogelDisk
        return XRENGINE_SampleShadowMapVogel(shadowMap, shadowCoord, bias, vogelTapCount, filterRadius);

    int clampedFilterSamples = clamp(filterSamples, 1, XRENGINE_MaxVogelShadowTaps);
    if (clampedFilterSamples <= 1)
        return XRENGINE_SampleShadowMapSimple(shadowMap, shadowCoord, bias);

    if (softShadowMode == 2) // ContactHardeningPcss
        return XRENGINE_SampleShadowMapCHSS(
            shadowMap,
            shadowCoord,
            bias,
            blockerSamples,
            clampedFilterSamples,
            blockerSearchRadius,
            lightSourceRadius,
            minPenumbra,
            maxPenumbra);

    if (softShadowMode == 1) // FixedPoisson
        return XRENGINE_SampleShadowMapSoft(shadowMap, shadowCoord, bias, clampedFilterSamples, filterRadius);

    if (clampedFilterSamples <= 4)
        return XRENGINE_SampleShadowMapTent4(shadowMap, shadowCoord, bias, filterRadius);

    return XRENGINE_SampleShadowMapPCF(shadowMap, shadowCoord, bias, 3);
}

float XRENGINE_SampleShadowMapFiltered(sampler2D shadowMap, vec3 shadowCoord, float bias, int requestedSamples, float filterRadius, int softShadowMode, float lightSourceRadius, int vogelTapCount)
{
    return XRENGINE_SampleShadowMapFiltered(
        shadowMap,
        shadowCoord,
        bias,
        requestedSamples,
        requestedSamples,
        filterRadius,
        filterRadius,
        softShadowMode,
        lightSourceRadius,
        0.0,
        filterRadius * 4.0,
        vogelTapCount);
}

float XRENGINE_SampleShadowMapArrayFiltered(
    sampler2DArray shadowMap,
    vec3 shadowCoord,
    float layer,
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
    if (softShadowMode == 3) // VogelDisk
        return XRENGINE_SampleShadowMapArrayVogel(shadowMap, shadowCoord, layer, bias, vogelTapCount, filterRadius);

    int clampedFilterSamples = clamp(filterSamples, 1, XRENGINE_MaxVogelShadowTaps);
    if (clampedFilterSamples <= 1)
        return XRENGINE_SampleShadowMapArraySimple(shadowMap, shadowCoord, layer, bias);

    if (softShadowMode == 2) // ContactHardeningPcss
        return XRENGINE_SampleShadowMapArrayCHSS(
            shadowMap,
            shadowCoord,
            layer,
            bias,
            blockerSamples,
            clampedFilterSamples,
            blockerSearchRadius,
            lightSourceRadius,
            minPenumbra,
            maxPenumbra);

    if (softShadowMode == 1) // FixedPoisson
        return XRENGINE_SampleShadowMapArraySoft(shadowMap, shadowCoord, layer, bias, clampedFilterSamples, filterRadius);

    if (clampedFilterSamples <= 4)
        return XRENGINE_SampleShadowMapArrayTent4(shadowMap, shadowCoord, layer, bias, filterRadius);

    return XRENGINE_SampleShadowMapArrayPCF(shadowMap, shadowCoord, layer, bias, 3);
}

float XRENGINE_SampleShadowMapArrayFiltered(sampler2DArray shadowMap, vec3 shadowCoord, float layer, float bias, int requestedSamples, float filterRadius, int softShadowMode, float lightSourceRadius, int vogelTapCount)
{
    return XRENGINE_SampleShadowMapArrayFiltered(
        shadowMap,
        shadowCoord,
        layer,
        bias,
        requestedSamples,
        requestedSamples,
        filterRadius,
        filterRadius,
        softShadowMode,
        lightSourceRadius,
        0.0,
        filterRadius * 4.0,
        vogelTapCount);
}

float XRENGINE_SampleShadowCubeFiltered(
    samplerCube shadowMap,
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
    if (softShadowMode == 3) // VogelDisk
        return XRENGINE_SampleShadowCubeVogel(shadowMap, shadowDir, compareDepth, bias, farPlaneDist, sampleRadius, vogelTapCount);

    int clampedFilterSamples = clamp(filterSamples, 1, XRENGINE_MaxVogelShadowTaps);
    if (clampedFilterSamples <= 1)
        return XRENGINE_SampleShadowCubeSimple(shadowMap, shadowDir, compareDepth, bias, farPlaneDist);

    if (softShadowMode == 2) // ContactHardeningPcss
        return XRENGINE_SampleShadowCubeCHSS(
            shadowMap,
            shadowDir,
            compareDepth,
            bias,
            farPlaneDist,
            blockerSearchRadius,
            blockerSamples,
            clampedFilterSamples,
            lightSourceRadius,
            minPenumbra,
            maxPenumbra);

    if (softShadowMode == 1 || clampedFilterSamples <= 4) // FixedPoisson or low-count
        return XRENGINE_SampleShadowCubeVogel(shadowMap, shadowDir, compareDepth, bias, farPlaneDist, sampleRadius, clampedFilterSamples);

    return XRENGINE_SampleShadowCubePCF(shadowMap, shadowDir, compareDepth, bias, farPlaneDist, sampleRadius);
}

float XRENGINE_SampleShadowCubeFiltered(samplerCube shadowMap, vec3 shadowDir, float compareDepth, float bias, float farPlaneDist, float sampleRadius, int requestedSamples, int softShadowMode, float lightSourceRadius, int vogelTapCount)
{
    return XRENGINE_SampleShadowCubeFiltered(
        shadowMap,
        shadowDir,
        compareDepth,
        bias,
        farPlaneDist,
        sampleRadius,
        sampleRadius,
        requestedSamples,
        requestedSamples,
        softShadowMode,
        lightSourceRadius,
        sampleRadius * 0.1,
        sampleRadius * 4.0,
        vogelTapCount);
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
