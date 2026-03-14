#version 450
#extension GL_OVR_multiview2 : require

#pragma snippet "NormalEncoding"

const float PI = 3.14159265359f;

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray Normal;
uniform sampler2DArray DepthView;

uniform vec4 ScaleFactors;
uniform float Bias = 0.05f;
uniform float Intensity = 1.0f;
uniform float ScreenWidth;
uniform float ScreenHeight;
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
    return viewSpacePosition.xyz / viewSpacePosition.w;
}

float ComputeObscurance(vec3 pos, vec3 normal, float radius, vec2 texCoord, mat4 projMatrix)
{
    float occlusion = 0.0f;
    int numSamples = 8;
    float stepAngle = 2.0f * PI / float(numSamples);

    for (int i = 0; i < numSamples; ++i)
    {
        float angle = stepAngle * float(i);
        vec2 sampleOffset = vec2(cos(angle), sin(angle)) * radius / vec2(ScreenWidth, ScreenHeight);

        vec2 uv = texCoord + sampleOffset;
        float depth = texture(DepthView, vec3(uv, gl_ViewID_OVR)).r;
        if (AOIsFarDepth(depth))
            continue;
        vec3 samplePos = ViewPosFromDepth(depth, uv, projMatrix);
        vec3 diff = samplePos - pos;

        float dist = length(diff);
        float dotProduct = max(dot(normal, normalize(diff)), 0.0f);
        occlusion += max(radius - dist, 0.0f) * dotProduct;
    }

    return occlusion / float(numSamples);
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

    vec3 normal = XRENGINE_ReadNormal(Normal, vec3(uv, gl_ViewID_OVR));
    vec3 viewNormal = normalize((inverse(inverseViewMatrix) * vec4(normal, 0.0f)).rgb);
    float depth = texture(DepthView, vec3(uv, gl_ViewID_OVR)).r;
    if (AOIsFarDepth(depth))
    {
        OutIntensity = 1.0f;
        return;
    }
    vec3 position = ViewPosFromDepth(depth, uv, projMatrix);

    float totalOcclusion = 0.0f;
    totalOcclusion += ComputeObscurance(position, viewNormal, ScaleFactors.x, uv, projMatrix);
    totalOcclusion += ComputeObscurance(position, viewNormal, ScaleFactors.y, uv, projMatrix);
    totalOcclusion += ComputeObscurance(position, viewNormal, ScaleFactors.z, uv, projMatrix);
    totalOcclusion += ComputeObscurance(position, viewNormal, ScaleFactors.w, uv, projMatrix);
    float obscurance = 0.25f * totalOcclusion;
    OutIntensity = clamp(1.0f - Bias * obscurance * Intensity, 0.0f, 1.0f);
}