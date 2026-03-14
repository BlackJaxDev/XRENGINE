#version 450

#include "AOCommon.glsl"
#pragma snippet "NormalEncoding"

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2D AmbientOcclusionTexture; // Raw AO
uniform sampler2D DepthView; // Depth
uniform sampler2D Normal; // Normal

uniform float DepthPhi = 4.0f;
uniform float NormalPhi = 64.0f;

void main()
{
    vec2 uv = FragPos.xy;
    if (uv.x > 1.0f || uv.y > 1.0f)
        discard;
    uv = uv * 0.5f + 0.5f;

    vec2 texelSize = 1.0f / vec2(textureSize(AmbientOcclusionTexture, 0));

    float centerAO = texture(AmbientOcclusionTexture, uv).r;
    float centerDepth = texture(DepthView, uv).r;
    if (AOIsFarDepth(centerDepth))
    {
        OutIntensity = 1.0f;
        return;
    }
    vec3 centerNormal = XRENGINE_ReadNormal(Normal, uv);

    float weightSum = 0.0f;
    float result = 0.0f;

    for (int x = -2; x <= 2; ++x)
    {
        for (int y = -2; y <= 2; ++y)
        {
            vec2 pixelOffset = vec2(float(x), float(y));
            vec2 sampleUV = uv + pixelOffset * texelSize;

            float sampleAO = texture(AmbientOcclusionTexture, sampleUV).r;
            float sampleDepth = texture(DepthView, sampleUV).r;
            if (AOIsFarDepth(sampleDepth))
                continue;
            vec3 sampleNormal = XRENGINE_ReadNormal(Normal, sampleUV);

            float depthWeight = exp(-abs(sampleDepth - centerDepth) * DepthPhi);
            float normalWeight = pow(max(dot(sampleNormal, centerNormal), 0.0f), NormalPhi);
            float spatialWeight = AOGaussianWeight(dot(pixelOffset, pixelOffset), 1.1952f);

            float weight = depthWeight * normalWeight * spatialWeight;
            result += sampleAO * weight;
            weightSum += weight;
        }
    }

    if (weightSum > 0.0f)
        result /= weightSum;
    else
        result = centerAO;

    OutIntensity = clamp(result, 0.0f, 1.0f);
}
