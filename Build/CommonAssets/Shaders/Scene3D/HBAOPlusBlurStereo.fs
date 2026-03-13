#version 460
#extension GL_OVR_multiview2 : require
#include "AOCommon.glsl"

#pragma snippet "NormalEncoding"

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray HBAOInputTexture;
uniform sampler2DArray DepthView;
uniform sampler2DArray Normal;

uniform vec2 BlurDirection = vec2(1.0f, 0.0f);
uniform int BlurRadius = 8;
uniform float BlurSharpness = 4.0f;
uniform bool BlurEnabled = true;
uniform bool UseInputNormals = true;

void main()
{
    vec2 uv = FragPos.xy;
    if (uv.x > 1.0f || uv.y > 1.0f)
        discard;
    uv = uv * 0.5f + 0.5f;

    float centerAO = texture(HBAOInputTexture, vec3(uv, gl_ViewID_OVR)).r;
    if (!BlurEnabled || BlurRadius <= 0)
    {
        OutIntensity = centerAO;
        return;
    }

    vec2 texelSize = 1.0f / textureSize(HBAOInputTexture, 0).xy;
    float centerDepth = texture(DepthView, vec3(uv, gl_ViewID_OVR)).r;
    vec3 centerNormal = XRENGINE_ReadNormal(Normal, vec3(uv, gl_ViewID_OVR));
    float sigma = max(float(BlurRadius) * 0.5f, 1.0f);
    float sharpness = max(BlurSharpness, 0.001f);

    float result = 0.0f;
    float weightSum = 0.0f;

    for (int offset = -16; offset <= 16; ++offset)
    {
        if (abs(offset) > BlurRadius)
            continue;

        vec2 sampleUV = uv + BlurDirection * texelSize * float(offset);
        float sampleAO = texture(HBAOInputTexture, vec3(sampleUV, gl_ViewID_OVR)).r;
        float sampleDepth = texture(DepthView, vec3(sampleUV, gl_ViewID_OVR)).r;
        vec3 sampleNormal = XRENGINE_ReadNormal(Normal, vec3(sampleUV, gl_ViewID_OVR));

        float spatialWeight = AOGaussianWeight(float(offset * offset), sigma);
        float depthWeight = exp(-abs(sampleDepth - centerDepth) * (24.0f * sharpness));
        float normalWeight = UseInputNormals
            ? pow(max(dot(sampleNormal, centerNormal), 0.0f), 1.0f + sharpness * 2.0f)
            : 1.0f;

        float weight = spatialWeight * depthWeight * normalWeight;
        result += sampleAO * weight;
        weightSum += weight;
    }

    OutIntensity = weightSum > 0.0f ? clamp(result / weightSum, 0.0f, 1.0f) : centerAO;
}