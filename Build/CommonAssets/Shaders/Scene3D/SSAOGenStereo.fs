#version 460
#extension GL_OVR_multiview2 : require
#include "AOCommon.glsl"
//#extension GL_EXT_multiview_tessellation_geometry_shader : enable

#pragma snippet "NormalEncoding"

const float PI = 3.14159265359f;
const float InvPI = 0.31831f;

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray Normal; //Normal
uniform sampler2D AONoiseTexture; // AO noise
uniform sampler2DArray DepthView; //Depth

uniform vec3 Samples[128];
uniform float Radius = 0.75f;
uniform float Power = 4.0f;
uniform vec2 NoiseScale;

uniform mat4 LeftEyeInverseViewMatrix;
uniform mat4 RightEyeInverseViewMatrix;

uniform mat4 LeftEyeProjMatrix;
uniform mat4 RightEyeProjMatrix;

void main()
{
    vec2 uv = FragPos.xy;
    if (uv.x > 1.0f || uv.y > 1.0f)
        discard;
    //Normalize uv from [-1, 1] to [0, 1]
    uv = uv * 0.5f + 0.5f;
    vec3 uvi = vec3(uv, gl_ViewID_OVR);
    bool leftEye = gl_ViewID_OVR == 0;
    mat4 InverseViewMatrix = leftEye ? LeftEyeInverseViewMatrix : RightEyeInverseViewMatrix;
    mat4 ProjMatrix = leftEye ? LeftEyeProjMatrix : RightEyeProjMatrix;
    
    vec3 Normal = XRENGINE_ReadNormal(Normal, uvi);
    float Depth = texture(DepthView, uvi).r;

    vec3 FragPosVS = AOViewPosFromDepth(Depth, uv, ProjMatrix);

    vec3 randomVec = vec3(texture(AONoiseTexture, uv * NoiseScale).rg * 2.0f - 1.0f, 0.0f);
    vec3 viewNormal = normalize((inverse(InverseViewMatrix) * vec4(Normal, 0.0f)).rgb);
    vec3 viewTangent = normalize(randomVec - viewNormal * dot(randomVec, viewNormal));
    vec3 viewBitangent = cross(viewNormal, viewTangent);
    mat3 TBN = mat3(viewTangent, viewBitangent, viewNormal);

    int kernelSize = 128;
    float bias = 0.025f;

    vec3 noiseSample;
    vec4 offset;
    float sampleDepth;
    float occlusion = 0.0f;

    for (int i = 0; i < kernelSize; ++i)
    {
        noiseSample = TBN * Samples[i];
        noiseSample = FragPosVS + noiseSample * Radius;

        offset = ProjMatrix * vec4(noiseSample, 1.0f);
        offset.xyz /= offset.w;
        offset.xyz = offset.xyz * 0.5f + 0.5f;

        sampleDepth = AOViewPosFromDepth(texture(DepthView, vec3(offset.xy, gl_ViewID_OVR)).r, offset.xy, ProjMatrix).z;

        occlusion += (sampleDepth >= noiseSample.z + bias ? smoothstep(0.0f, 1.0f, Radius / abs(FragPosVS.z - sampleDepth)) : 0.0f);
    }

    OutIntensity = pow(1.0f - (occlusion / kernelSize), Power);
}
