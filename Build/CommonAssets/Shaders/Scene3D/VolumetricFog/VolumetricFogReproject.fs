#version 450

// Volumetric fog temporal reprojection.
//
// Reads current half-res scatter, previous half-res history, and current
// half-res depth. Writes a stabilized half-res fog texture consumed by the
// bilateral upscale pass.

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D VolumetricFogHalfScatter;
uniform sampler2D VolumetricFogHalfHistory;
uniform sampler2D VolumetricFogHalfDepth;

uniform vec3 CameraPosition;
uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform int DepthMode;

uniform bool VolumetricFogHistoryReady;
uniform mat4 VolumetricFogPreviousViewProjection;
uniform vec2 VolumetricFogHistoryTexelSize;
uniform float VolumetricFogMaxDistance;
uniform float VolumetricFogTemporalAlpha;
uniform float VolumetricFogDepthRejectThreshold;
uniform int VolumetricFogDebugMode;

float ResolveDepth(float depth)
{
    return DepthMode == 1 ? (1.0f - depth) : depth;
}

bool IsValidUV(vec2 uv)
{
    return all(greaterThanEqual(uv, vec2(0.0f))) && all(lessThanEqual(uv, vec2(1.0f)));
}

vec3 WorldPosFromDepthRaw(float rawDepth, vec2 uv)
{
    vec4 clipSpacePosition = vec4(vec3(uv, rawDepth) * 2.0f - 1.0f, 1.0f);
    vec4 viewSpacePosition = InverseProjMatrix * clipSpacePosition;
    viewSpacePosition /= max(abs(viewSpacePosition.w), 1e-5f) * sign(viewSpacePosition.w == 0.0f ? 1.0f : viewSpacePosition.w);
    return (InverseViewMatrix * viewSpacePosition).xyz;
}

float LinearEyeDistance(float rawDepth, vec2 uv)
{
    vec4 clipSpacePosition = vec4(vec3(uv, rawDepth) * 2.0f - 1.0f, 1.0f);
    vec4 viewSpacePosition = InverseProjMatrix * clipSpacePosition;
    float safeW = max(abs(viewSpacePosition.w), 1e-5f);
    return abs(viewSpacePosition.z / safeW);
}

vec3 RepresentativeFogWorldPos(float rawDepth, vec2 uv)
{
    float resolvedDepth = ResolveDepth(rawDepth);
    float farRawDepth = DepthMode == 1 ? 0.0f : 1.0f;
    vec3 rayEnd = resolvedDepth >= 0.999999f
        ? WorldPosFromDepthRaw(farRawDepth, uv)
        : WorldPosFromDepthRaw(rawDepth, uv);

    vec3 rayVector = rayEnd - CameraPosition;
    float rayVectorLength = max(length(rayVector), 1e-5f);
    vec3 rayDir = rayVector / rayVectorLength;

    float rayLength = max(VolumetricFogMaxDistance, 0.0f);
    if (resolvedDepth < 0.999999f)
        rayLength = min(rayLength, rayVectorLength);

    return CameraPosition + rayDir * (rayLength * 0.5f);
}

bool ProjectToPreviousUv(vec3 worldPos, out vec2 previousUv)
{
    vec4 previousClip = VolumetricFogPreviousViewProjection * vec4(worldPos, 1.0f);
    if (abs(previousClip.w) <= 1e-5f)
    {
        previousUv = vec2(0.0f);
        return false;
    }

    vec2 previousNdc = previousClip.xy / previousClip.w;
    previousUv = previousNdc * 0.5f + 0.5f;
    return IsValidUV(previousUv);
}

vec4 ClipToAABB(vec4 value, vec4 boundsMin, vec4 boundsMax)
{
    vec4 center = 0.5f * (boundsMin + boundsMax);
    vec4 extents = 0.5f * (boundsMax - boundsMin) + vec4(1e-5f);
    vec4 offset = value - center;
    vec4 scale = abs(extents / max(abs(offset), vec4(1e-5f)));
    float clipAmount = clamp(min(min(scale.x, scale.y), min(scale.z, scale.w)), 0.0f, 1.0f);
    return center + offset * clipAmount;
}

bool IsNeutralFog(vec4 fog)
{
    return fog.a >= 0.9999f && all(lessThanEqual(abs(fog.rgb), vec3(1e-5f)));
}

void ComputeNeighborhoodBounds(vec2 uv, out vec4 boundsMin, out vec4 boundsMax)
{
    boundsMin = vec4(1.0e20f);
    boundsMax = vec4(-1.0e20f);

    for (int offsetY = -1; offsetY <= 1; ++offsetY)
    {
        for (int offsetX = -1; offsetX <= 1; ++offsetX)
        {
            vec2 sampleUv = clamp(uv + vec2(float(offsetX), float(offsetY)) * VolumetricFogHistoryTexelSize, vec2(0.0f), vec2(1.0f));
            vec4 sampleValue = texture(VolumetricFogHalfScatter, sampleUv);
            boundsMin = min(boundsMin, sampleValue);
            boundsMax = max(boundsMax, sampleValue);
        }
    }
}

void main()
{
    vec2 ndc = FragPos.xy;
    if (ndc.x > 1.0f || ndc.y > 1.0f)
        discard;

    vec2 uv = ndc * 0.5f + 0.5f;
    vec4 currentFog = texture(VolumetricFogHalfScatter, uv);

    if (IsNeutralFog(currentFog))
    {
        OutColor = currentFog;
        return;
    }

    if (!VolumetricFogHistoryReady || VolumetricFogMaxDistance <= 0.0f || VolumetricFogDebugMode != 0)
    {
        OutColor = currentFog;
        return;
    }

    float rawDepth = texture(VolumetricFogHalfDepth, uv).r;
    vec3 representativeWorldPos = RepresentativeFogWorldPos(rawDepth, uv);

    vec2 previousUv;
    if (!ProjectToPreviousUv(representativeWorldPos, previousUv))
    {
        OutColor = currentFog;
        return;
    }

    float currentLinearDepth = LinearEyeDistance(rawDepth, uv);
    float reprojectedRawDepth = texture(VolumetricFogHalfDepth, previousUv).r;
    float reprojectedLinearDepth = LinearEyeDistance(reprojectedRawDepth, previousUv);
    float depthDelta = abs(currentLinearDepth - reprojectedLinearDepth);
    float adaptiveDepthThreshold = max(VolumetricFogDepthRejectThreshold, currentLinearDepth * 0.02f);
    float depthConfidence = 1.0f - smoothstep(adaptiveDepthThreshold, adaptiveDepthThreshold * 4.0f, depthDelta);
    if (depthConfidence <= 0.0f)
    {
        OutColor = currentFog;
        return;
    }

    vec4 historyFog = texture(VolumetricFogHalfHistory, previousUv);

    vec4 boundsMin;
    vec4 boundsMax;
    ComputeNeighborhoodBounds(uv, boundsMin, boundsMax);
    vec4 clippedHistory = ClipToAABB(historyFog, boundsMin, boundsMax);

    float clampDistance = length(historyFog - clippedHistory);
    float clampConfidence = 1.0f - smoothstep(0.03f, 0.30f, clampDistance);
    float historyWeight = clamp(VolumetricFogTemporalAlpha, 0.0f, 0.98f) * depthConfidence * clampConfidence;

    OutColor = mix(currentFog, clippedHistory, historyWeight);
}
