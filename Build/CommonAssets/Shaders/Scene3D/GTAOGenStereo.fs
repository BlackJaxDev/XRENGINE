#version 460
#extension GL_OVR_multiview2 : require

#pragma snippet "NormalEncoding"

const float PI = 3.14159265359f;
const float HALF_PI = 1.57079632679f;

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray Normal;
uniform sampler2DArray DepthView;

uniform float Radius = 2.0f;
uniform float Bias = 0.05f;
uniform float Power = 1.0f;
uniform int SliceCount = 3;
uniform int StepsPerSlice = 6;
uniform float FalloffStartRatio = 0.4f;
uniform bool UseInputNormals = true;
uniform int DepthMode;

uniform mat4 LeftEyeInverseViewMatrix;
uniform mat4 RightEyeInverseViewMatrix;
uniform mat4 LeftEyeProjMatrix;
uniform mat4 RightEyeProjMatrix;

bool AOIsFarDepth(float depth)
{
    const float eps = 1e-6f;
    return DepthMode == 1 ? depth <= eps : depth >= 1.0f - eps;
}

vec3 ViewPosFromDepth(float depth, vec2 uv, mat4 projMatrix)
{
    vec4 clipSpacePosition = vec4(vec3(uv, depth) * 2.0f - 1.0f, 1.0f);
    vec4 viewSpacePosition = inverse(projMatrix) * clipSpacePosition;
    return viewSpacePosition.xyz / max(viewSpacePosition.w, 1e-5f);
}

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
    float depthX = texture(DepthView, vec3(uvX, gl_ViewID_OVR)).r;
    float depthY = texture(DepthView, vec3(uvY, gl_ViewID_OVR)).r;
    if (AOIsFarDepth(depthX) || AOIsFarDepth(depthY))
        return vec3(0.0f, 0.0f, 1.0f);
    vec3 posX = ViewPosFromDepth(depthX, uvX, projMatrix);
    vec3 posY = ViewPosFromDepth(depthY, uvY, projMatrix);
    return normalize(cross(posX - centerPos, posY - centerPos));
}

float ComputeRadiusPixels(float radiusVS, float viewDepth, mat4 projMatrix)
{
    float projScale = abs(projMatrix[1][1]) * 0.5f * float(textureSize(DepthView, 0).y);
    return clamp(radiusVS * projScale / max(abs(viewDepth), 1e-3f), 1.0f, 192.0f);
}

float InterleavedGradientNoise(vec2 pixel)
{
    return fract(52.9829189f * fract(dot(pixel, vec2(0.06711056f, 0.00583715f))));
}

vec3 ProjectOntoPlane(vec3 value, vec3 planeNormal)
{
    return value - planeNormal * dot(value, planeNormal);
}

float IntegrateArc(float horizonAngle, float normalAngle)
{
    float h = clamp(horizonAngle, 0.0f, HALF_PI);
    float n = clamp(normalAngle, -HALF_PI, HALF_PI);
    return 0.25f * (-cos(2.0f * h - n) + cos(n) + 2.0f * h * sin(n));
}

float UpdateHorizonAngle(
    float currentHorizon,
    vec3 centerPos,
    vec2 sampleUV,
    float radiusVS,
    float falloffStart,
    vec3 planeForward,
    vec3 planeSide,
    vec3 planeNormal,
    mat4 projMatrix)
{
    if (sampleUV.x <= 0.0f || sampleUV.x >= 1.0f || sampleUV.y <= 0.0f || sampleUV.y >= 1.0f)
        return currentHorizon;

    float sampleDepth = texture(DepthView, vec3(sampleUV, gl_ViewID_OVR)).r;
    if (AOIsFarDepth(sampleDepth))
        return currentHorizon;
    vec3 samplePos = ViewPosFromDepth(sampleDepth, sampleUV, projMatrix);
    vec3 toSample = samplePos - centerPos;
    float distanceSq = dot(toSample, toSample);
    if (distanceSq <= 1e-6f)
        return currentHorizon;

    float distance = sqrt(distanceSq);
    if (distance > radiusVS)
        return currentHorizon;

    vec3 projected = ProjectOntoPlane(toSample / distance, planeNormal);
    float projectedLength = length(projected);
    if (projectedLength <= 1e-5f)
        return currentHorizon;

    projected /= projectedLength;

    float forward = dot(projected, planeForward);
    if (forward <= 1e-5f)
        return currentHorizon;

    float side = abs(dot(projected, planeSide));
    float horizonAngle = atan(side, forward);
    float attenuation = 1.0f - smoothstep(falloffStart, radiusVS, distance);
    float weightedHorizon = mix(HALF_PI, horizonAngle, attenuation);
    float biasedHorizon = clamp(weightedHorizon + Bias, 0.0f, HALF_PI);

    return min(currentHorizon, biasedHorizon);
}

vec3 ComputeSliceViewDirection(vec2 uv, float depth, vec3 centerPos, vec2 sliceDirection, mat4 projMatrix)
{
    vec2 texelSize = 1.0f / textureSize(DepthView, 0).xy;
    vec2 offsetUv = clamp(uv + sliceDirection * texelSize * 2.0f, vec2(0.0f), vec2(1.0f));
    float offsetDepth = texture(DepthView, vec3(offsetUv, gl_ViewID_OVR)).r;
    if (AOIsFarDepth(offsetDepth))
        return normalize(vec3(sliceDirection, 0.0f));
    vec3 offsetPos = ViewPosFromDepth(offsetDepth, offsetUv, projMatrix);
    vec3 sliceVector = offsetPos - centerPos;
    float sliceLengthSq = dot(sliceVector, sliceVector);
    if (sliceLengthSq <= 1e-6f)
        return normalize(vec3(sliceDirection, 0.0f));

    return normalize(sliceVector);
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
    if (AOIsFarDepth(depth))
    {
        OutIntensity = 1.0f;
        return;
    }
    vec3 centerPos = ViewPosFromDepth(depth, uv, projMatrix);
    vec3 centerNormal = GetViewNormal(uv, centerPos, inverseViewMatrix, projMatrix);

    float radiusVS = max(Radius, 0.001f);
    float radiusPixels = ComputeRadiusPixels(radiusVS, centerPos.z, projMatrix);
    float falloffStart = clamp(FalloffStartRatio, 0.0f, 1.0f) * radiusVS;
    vec2 texelSize = 1.0f / textureSize(DepthView, 0).xy;
    vec3 planeForwardBase = -normalize(centerPos);

    int sliceCount = clamp(SliceCount, 1, 8);
    int stepCount = clamp(StepsPerSlice, 1, 16);
    float baseAngle = (InterleavedGradientNoise(floor(gl_FragCoord.xy) + vec2(float(gl_ViewID_OVR) * 13.0f, 0.0f)) - 0.5f) * (PI / float(sliceCount));
    float stepJitter = InterleavedGradientNoise(floor(gl_FragCoord.xy) + vec2(17.0f, 43.0f + float(gl_ViewID_OVR) * 19.0f));

    float visibility = 0.0f;

    for (int sliceIndex = 0; sliceIndex < sliceCount; ++sliceIndex)
    {
        float angle = baseAngle + (PI * float(sliceIndex)) / float(sliceCount);
        vec2 direction = vec2(cos(angle), sin(angle));
        vec3 planeSide = ComputeSliceViewDirection(uv, depth, centerPos, direction, projMatrix);
        vec3 planeNormal = cross(planeSide, planeForwardBase);
        float planeNormalLength = length(planeNormal);
        if (planeNormalLength <= 1e-5f)
        {
            visibility += 1.0f;
            continue;
        }

        planeNormal /= planeNormalLength;
        vec3 projectedNormal = ProjectOntoPlane(centerNormal, planeNormal);
        float projectedNormalLength = length(projectedNormal);
        if (projectedNormalLength <= 1e-5f)
        {
            visibility += 1.0f;
            continue;
        }

        projectedNormal /= projectedNormalLength;
        float normalAngle = atan(dot(projectedNormal, planeSide), dot(projectedNormal, planeForwardBase));

        float forwardHorizon = HALF_PI;
        float backwardHorizon = HALF_PI;

        for (int stepIndex = 0; stepIndex < stepCount; ++stepIndex)
        {
            float stepT = (float(stepIndex) + stepJitter) / float(stepCount);
            vec2 offset = direction * texelSize * radiusPixels * stepT;
            forwardHorizon = UpdateHorizonAngle(forwardHorizon, centerPos, uv + offset, radiusVS, falloffStart, planeForwardBase, planeSide, planeNormal, projMatrix);
            backwardHorizon = UpdateHorizonAngle(backwardHorizon, centerPos, uv - offset, radiusVS, falloffStart, planeForwardBase, -planeSide, planeNormal, projMatrix);
        }

        float sliceVisibility = projectedNormalLength * (
            IntegrateArc(forwardHorizon, normalAngle) +
            IntegrateArc(backwardHorizon, -normalAngle));
        visibility += clamp(sliceVisibility, 0.0f, 1.0f);
    }

    visibility = clamp(visibility / float(sliceCount), 0.0f, 1.0f);
    OutIntensity = pow(visibility, max(Power, 0.001f));
}