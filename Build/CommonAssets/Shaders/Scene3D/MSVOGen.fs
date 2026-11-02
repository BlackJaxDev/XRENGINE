#version 450

#include "AOCommon.glsl"
#pragma snippet "NormalEncoding"

const float PI = 3.14159265359f;
const float InvPI = 0.31831f;

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2D Normal; //Normal
uniform sampler2D DepthView; //Depth

uniform vec4 ScaleFactors; // Fractions of Radius
uniform float Radius = 2.2f;
uniform float Bias = 0.05f;
uniform float Intensity = 1.0f;
uniform float ScreenWidth;
uniform float ScreenHeight;

uniform mat4 ViewMatrix;
uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform mat4 ProjMatrix;

vec3 ViewPosFromDepth(float depth, vec2 uv)
{
    return AOViewPosFromDepth(depth, uv, InverseProjMatrix);
}

float ComputeObscurance(vec3 pos, vec3 normal, float radius, vec2 texCoord)
 {
    // Project the view-space radius, and keep at least one pixel so each scale finds a neighbor.
    vec2 pixel = 1.0f / max(vec2(ScreenWidth, ScreenHeight), vec2(1.0f));
    vec2 uvRadius = max(radius * 0.5f * vec2(abs(ProjMatrix[0][0]), abs(ProjMatrix[1][1])) / max(abs(pos.z), 1e-3f), pixel);
    float occlusion = 0.0f;

    int numSamples = 8;
    float stepAngle = 2.0f * PI / float(numSamples);

    for (int i = 0; i < numSamples; ++i)
    {
        float angle = stepAngle * float(i);
        // Samples spread inside the radius. A sample on the radius itself has no falloff weight.
        vec2 sampleOffset = vec2(cos(angle), sin(angle)) * uvRadius * (float(i) + 0.5f) / float(numSamples);

        vec2 uv = texCoord + sampleOffset;
        float depth = texture(DepthView, uv).r;
        if (AOIsFarDepth(depth))
            continue;
        vec3 samplePos = ViewPosFromDepth(depth, uv);
        vec3 diff = samplePos - pos;

        float dist = length(diff);
        if (dist <= 1e-5f)
            continue;
        float facing = max(dot(normal, diff / dist) - Bias, 0.0f);
        occlusion += facing * clamp(1.0f - dist / radius, 0.0f, 1.0f);
    }

    return occlusion / float(numSamples);
}

void main()
{
    if (FragPos.x > 1.0f || FragPos.y > 1.0f)
        discard;
    vec2 uv = AOTextureUVFromFragPos(FragPos);
    
    vec3 normal = XRENGINE_ReadNormal(Normal, uv);
    vec3 viewNormal = normalize((ViewMatrix * vec4(normal, 0.0f)).rgb);
    float depth = texture(DepthView, uv).r;
    if (AOIsFarDepth(depth))
    {
        OutIntensity = 1.0f;
        return;
    }
    vec3 position = ViewPosFromDepth(depth, uv);

    float totalOcclusion = 0.0f;
    totalOcclusion += ComputeObscurance(position, viewNormal, ScaleFactors.x * Radius, uv);
    totalOcclusion += ComputeObscurance(position, viewNormal, ScaleFactors.y * Radius, uv);
    totalOcclusion += ComputeObscurance(position, viewNormal, ScaleFactors.z * Radius, uv);
    totalOcclusion += ComputeObscurance(position, viewNormal, ScaleFactors.w * Radius, uv);
    float obscurance = 0.25f * totalOcclusion;
    OutIntensity = clamp(1.0f - obscurance * Intensity, 0.0f, 1.0f);
}
