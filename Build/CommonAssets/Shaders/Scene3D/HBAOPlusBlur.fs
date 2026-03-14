#version 450

#pragma snippet "NormalEncoding"

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2D HBAOInputTexture;
uniform sampler2D DepthView;
uniform sampler2D Normal;

uniform vec2 BlurDirection = vec2(1.0f, 0.0f);
uniform int BlurRadius = 8;
uniform float BlurSharpness = 4.0f;
uniform bool BlurEnabled = true;
uniform bool UseInputNormals = true;
uniform int DepthMode;

bool AOIsFarDepth(float depth)
{
    const float eps = 1e-6f;
    return DepthMode == 1 ? depth <= eps : depth >= 1.0f - eps;
}

void main()
{
    vec2 uv = FragPos.xy;
    if (uv.x > 1.0f || uv.y > 1.0f)
        discard;
    uv = uv * 0.5f + 0.5f;

    float centerAO = texture(HBAOInputTexture, uv).r;
    if (!BlurEnabled || BlurRadius <= 0)
    {
        OutIntensity = centerAO;
        return;
    }

    vec2 texelSize = 1.0f / vec2(textureSize(HBAOInputTexture, 0));
    float centerDepth = texture(DepthView, uv).r;
    if (AOIsFarDepth(centerDepth))
    {
        OutIntensity = 1.0f;
        return;
    }
    vec3 centerNormal = XRENGINE_ReadNormal(Normal, uv);
    float sigma = max(float(BlurRadius) * 0.5f, 1.0f);
    float sharpness = max(BlurSharpness, 0.001f);

    float result = 0.0f;
    float weightSum = 0.0f;

    for (int offset = -16; offset <= 16; ++offset)
    {
        if (abs(offset) > BlurRadius)
            continue;

        vec2 sampleUV = uv + BlurDirection * texelSize * float(offset);
        float sampleAO = texture(HBAOInputTexture, sampleUV).r;
        float sampleDepth = texture(DepthView, sampleUV).r;
        if (AOIsFarDepth(sampleDepth))
            continue;
        vec3 sampleNormal = XRENGINE_ReadNormal(Normal, sampleUV);

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