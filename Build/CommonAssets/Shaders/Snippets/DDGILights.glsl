#ifndef XR_DDGI_LIGHTS_INCLUDED
#define XR_DDGI_LIGHTS_INCLUDED

#pragma snippet "DDGIBindings"
#pragma snippet "LightAttenuation"

const uint XR_DDGI_MAX_DIRECT_LIGHTS = 255u;
const float XR_DDGI_LIGHT_DIRECTIONAL = 0.0;
const float XR_DDGI_LIGHT_POINT = 1.0;
const float XR_DDGI_LIGHT_SPOT = 2.0;

struct DDGILightRecord
{
    vec4 positionOrDirectionAndType;
    vec4 colorAndIntensity;
    vec4 directionAndInnerCutoff;
    vec4 radiusOuterExponentAndFlags;
};

// Binding eight is a UBO namespace slot on OpenGL and a distinct set-zero
// Vulkan descriptor. SSBO bindings zero through seven remain unchanged.
layout(std140, XR_DDGI_UNIFORM_BINDING(8)) uniform DDGILightBlock
{
    DDGILightRecord gDDGILightRecords[XR_DDGI_MAX_DIRECT_LIGHTS + 1u];
};

vec3 DDGILightTransmittance(vec3 origin, vec3 direction, float tMin, float tMax, float castsShadows)
{
    return castsShadows > 0.5
        ? DDGIShadowTransmittance(origin, direction, tMin, tMax)
        : vec3(1.0);
}

vec3 DDGIEvaluateDirectLighting(vec3 position, vec3 normal)
{
    DDGILightRecord header = gDDGILightRecords[0];
    uint directionalCount = min(uint(max(header.positionOrDirectionAndType.x, 0.0)), XR_DDGI_MAX_DIRECT_LIGHTS);
    uint pointCount = min(uint(max(header.positionOrDirectionAndType.y, 0.0)), XR_DDGI_MAX_DIRECT_LIGHTS - directionalCount);
    uint spotCount = min(uint(max(header.positionOrDirectionAndType.z, 0.0)), XR_DDGI_MAX_DIRECT_LIGHTS - directionalCount - pointCount);
    vec3 directIrradiance = vec3(0.0);
    float shadowBias = max(uNormalBias, 0.001);
    uint index = 1u;

    for (uint i = 0u; i < directionalCount; i++, index++)
    {
        DDGILightRecord light = gDDGILightRecords[index];
        vec3 lightDirection = -light.positionOrDirectionAndType.xyz;
        float lightLength = length(lightDirection);
        if (lightLength <= 1e-6 || light.colorAndIntensity.w <= 0.0)
            continue;
        lightDirection /= lightLength;
        float nDotL = max(dot(normal, lightDirection), 0.0);
        if (nDotL <= 0.0)
            continue;
        directIrradiance += max(light.colorAndIntensity.rgb, vec3(0.0)) * (light.colorAndIntensity.w * nDotL)
            * DDGILightTransmittance(position + normal * shadowBias, lightDirection, shadowBias, 1e20, light.radiusOuterExponentAndFlags.w);
    }

    for (uint i = 0u; i < pointCount; i++, index++)
    {
        DDGILightRecord light = gDDGILightRecords[index];
        vec3 toLight = light.positionOrDirectionAndType.xyz - position;
        float distanceToLight = length(toLight);
        float radius = light.radiusOuterExponentAndFlags.x;
        if (radius <= 1e-6 || distanceToLight <= 1e-6 || distanceToLight >= radius || light.colorAndIntensity.w <= 0.0)
            continue;
        vec3 lightDirection = toLight / distanceToLight;
        float nDotL = max(dot(normal, lightDirection), 0.0);
        if (nDotL <= 0.0)
            continue;
        float attenuation = XRENGINE_Attenuate(distanceToLight, radius);
        directIrradiance += max(light.colorAndIntensity.rgb, vec3(0.0)) * (light.colorAndIntensity.w * attenuation * nDotL)
            * DDGILightTransmittance(position + normal * shadowBias, lightDirection, shadowBias, max(distanceToLight - shadowBias, shadowBias), light.radiusOuterExponentAndFlags.w);
    }

    for (uint i = 0u; i < spotCount; i++, index++)
    {
        DDGILightRecord light = gDDGILightRecords[index];
        vec3 toLight = light.positionOrDirectionAndType.xyz - position;
        float distanceToLight = length(toLight);
        float radius = light.radiusOuterExponentAndFlags.x;
        if (radius <= 1e-6 || distanceToLight <= 1e-6 || distanceToLight >= radius || light.colorAndIntensity.w <= 0.0)
            continue;
        vec3 lightDirection = toLight / distanceToLight;
        vec3 spotDirection = light.directionAndInnerCutoff.xyz;
        float spotDirectionLength = length(spotDirection);
        if (spotDirectionLength <= 1e-6)
            continue;
        float cosine = dot(lightDirection, -spotDirection / spotDirectionLength);
        float outerCutoff = light.radiusOuterExponentAndFlags.y;
        float innerCutoff = max(light.directionAndInnerCutoff.w, outerCutoff + 1e-5);
        if (cosine <= outerCutoff)
            continue;
        float spotAmount = smoothstep(0.0, 1.0, clamp((cosine - outerCutoff) / (innerCutoff - outerCutoff), 0.0, 1.0));
        float nDotL = max(dot(normal, lightDirection), 0.0);
        if (nDotL <= 0.0)
            continue;
        float attenuation = XRENGINE_Attenuate(distanceToLight, radius) * spotAmount * pow(max(cosine, 0.0), max(light.radiusOuterExponentAndFlags.z, 0.0));
        directIrradiance += max(light.colorAndIntensity.rgb, vec3(0.0)) * (light.colorAndIntensity.w * attenuation * nDotL)
            * DDGILightTransmittance(position + normal * shadowBias, lightDirection, shadowBias, max(distanceToLight - shadowBias, shadowBias), light.radiusOuterExponentAndFlags.w);
    }
    return directIrradiance;
}

#endif
