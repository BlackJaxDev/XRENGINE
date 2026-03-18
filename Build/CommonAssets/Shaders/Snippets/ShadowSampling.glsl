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

float XRENGINE_BlendShadowFilter(float hardShadow, float filteredShadow, float depthDelta, float maxBlurDist)
{
    float normDist = clamp(depthDelta, 0.0, maxBlurDist) / maxBlurDist;
    return mix(hardShadow, filteredShadow, normDist);
}

float XRENGINE_CalculateShadowBias(float NdotL, float maxBias, float minBias)
{
    return max(maxBias * (1.0 - NdotL), minBias);
}
