#version 460
#extension GL_OVR_multiview2 : require
#include "AOCommon.glsl"

#pragma snippet "NormalEncoding"

const float PI = 3.14159265359f;

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray Normal;
uniform sampler2DArray DepthView;

uniform float Radius = 2.0f;
uniform float Bias = 0.1f;
uniform float Power = 2.0f;
uniform float DetailAO = 0.0f;
uniform bool UseInputNormals = true;
uniform float MetersToViewSpaceUnits = 1.0f;

uniform mat4 LeftEyeInverseViewMatrix;
uniform mat4 RightEyeInverseViewMatrix;
uniform mat4 LeftEyeProjMatrix;
uniform mat4 RightEyeProjMatrix;

vec3 GetViewNormal(vec2 uv, vec3 centerPos, mat4 inverseViewMatrix, mat4 projMatrix)
{
    if (UseInputNormals)
    {
        vec3 worldNormal = XRENGINE_ReadNormal(Normal, vec3(uv, gl_ViewID_OVR));
        return normalize((inverse(inverseViewMatrix) * vec4(worldNormal, 0.0f)).rgb);
    }

    vec2 texelSize = 1.0f / textureSize(DepthView, 0).xy;
    vec2 uvX = clamp(uv + vec2(texelSize.x, 0.0f), vec2(0.0f), vec2(1.0f));
    vec2 uvY = clamp(uv + vec2(0.0f, texelSize.y), vec2(0.0f), vec2(1.0f));
    vec3 posX = AOViewPosFromDepth(texture(DepthView, vec3(uvX, gl_ViewID_OVR)).r, uvX, projMatrix);
    vec3 posY = AOViewPosFromDepth(texture(DepthView, vec3(uvY, gl_ViewID_OVR)).r, uvY, projMatrix);
    return normalize(cross(posX - centerPos, posY - centerPos));
}

float ComputeRadiusPixels(float radiusVS, float viewDepth, mat4 projMatrix)
{
    float projScale = abs(projMatrix[1][1]) * 0.5f * float(textureSize(DepthView, 0).y);
    return clamp(radiusVS * projScale / max(abs(viewDepth), 1e-3f), 1.0f, 160.0f);
}

float SampleOcclusion(vec3 centerPos, vec3 centerNormal, vec2 sampleUV, float radiusVS, mat4 projMatrix)
{
    if (sampleUV.x <= 0.0f || sampleUV.x >= 1.0f || sampleUV.y <= 0.0f || sampleUV.y >= 1.0f)
        return 0.0f;

    vec3 samplePos = AOViewPosFromDepth(texture(DepthView, vec3(sampleUV, gl_ViewID_OVR)).r, sampleUV, projMatrix);
    vec3 toSample = samplePos - centerPos;
    float distanceSq = dot(toSample, toSample);
    if (distanceSq <= 1e-6f)
        return 0.0f;

    float distance = sqrt(distanceSq);
    if (distance > radiusVS)
        return 0.0f;

    vec3 sampleDir = toSample / distance;
    float angular = max(dot(centerNormal, sampleDir) - Bias, 0.0f);
    float attenuation = 1.0f - clamp(distance / radiusVS, 0.0f, 1.0f);
    return angular * attenuation;
}

void main()
{
    vec2 uv = FragPos.xy;
    if (uv.x > 1.0f || uv.y > 1.0f)
        discard;
    uv = uv * 0.5f + 0.5f;

    bool leftEye = gl_ViewID_OVR == 0;
    mat4 inverseViewMatrix = leftEye ? LeftEyeInverseViewMatrix : RightEyeInverseViewMatrix;
    mat4 projMatrix = leftEye ? LeftEyeProjMatrix : RightEyeProjMatrix;

    float depth = texture(DepthView, vec3(uv, gl_ViewID_OVR)).r;
    vec3 centerPos = AOViewPosFromDepth(depth, uv, projMatrix);
    vec3 centerNormal = GetViewNormal(uv, centerPos, inverseViewMatrix, projMatrix);

    float radiusVS = max(Radius * max(MetersToViewSpaceUnits, 0.001f), 0.001f);
    float radiusPixels = ComputeRadiusPixels(radiusVS, centerPos.z, projMatrix);
    vec2 texelSize = 1.0f / textureSize(DepthView, 0).xy;

    ivec2 tile = ivec2(gl_FragCoord.xy) & ivec2(3);
    float baseAngle = (float(tile.x + tile.y * 4) / 16.0f) * (2.0f * PI);
    float phase = (float((tile.x * 5 + tile.y * 3) & 3) + 0.5f) * 0.25f;

    const int DirectionCount = 8;
    const int StepCount = 4;
    float coarseOcclusion = 0.0f;

    for (int directionIndex = 0; directionIndex < DirectionCount; ++directionIndex)
    {
        float angle = baseAngle + (2.0f * PI * float(directionIndex)) / float(DirectionCount);
        vec2 sampleDirection = vec2(cos(angle), sin(angle));
        float directionHorizon = 0.0f;

        for (int stepIndex = 0; stepIndex < StepCount; ++stepIndex)
        {
            float sampleT = (float(stepIndex) + phase + 0.5f) / float(StepCount);
            vec2 sampleUV = uv + sampleDirection * texelSize * radiusPixels * sampleT;
            directionHorizon = max(directionHorizon, SampleOcclusion(centerPos, centerNormal, sampleUV, radiusVS, projMatrix));
        }

        coarseOcclusion += directionHorizon;
    }

    coarseOcclusion /= float(DirectionCount);

    float detailOcclusion = 0.0f;
    if (DetailAO > 0.0f)
    {
        float detailRadiusVS = max(radiusVS * 0.35f, 0.02f);
        float detailRadiusPixels = max(radiusPixels * 0.35f, 1.0f);
        const int DetailDirections = 4;

        for (int detailIndex = 0; detailIndex < DetailDirections; ++detailIndex)
        {
            float angle = baseAngle + (2.0f * PI * float(detailIndex)) / float(DetailDirections);
            vec2 sampleDirection = vec2(cos(angle), sin(angle));
            vec2 sampleUV = uv + sampleDirection * texelSize * detailRadiusPixels;
            detailOcclusion += SampleOcclusion(centerPos, centerNormal, sampleUV, detailRadiusVS, projMatrix);
        }

        detailOcclusion /= float(DetailDirections);
        detailOcclusion *= clamp(DetailAO, 0.0f, 5.0f) * 0.35f;
    }

    float visibility = clamp(1.0f - coarseOcclusion - detailOcclusion, 0.0f, 1.0f);
    OutIntensity = pow(visibility, max(Power, 0.001f));
}