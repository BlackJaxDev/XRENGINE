#version 460
#extension GL_OVR_multiview2 : require

#pragma snippet "NormalEncoding"

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray GTAOInputTexture;
uniform sampler2DArray DepthView;
uniform sampler2DArray Normal;

uniform vec2 BlurDirection = vec2(1.0f, 0.0f);
uniform int DenoiseRadius = 4;
uniform float DenoiseSharpness = 4.0f;
uniform bool DenoiseEnabled = true;
uniform bool UseInputNormals = true;
uniform bool UseNormalWeightedBlur = true;
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

    float centerAO = texture(GTAOInputTexture, vec3(uv, gl_ViewID_OVR)).r;
    if (!DenoiseEnabled || DenoiseRadius <= 0)
    {
        OutIntensity = centerAO;
        return;
    }

    vec2 texelSize = 1.0f / textureSize(GTAOInputTexture, 0).xy;
    float centerDepth = texture(DepthView, vec3(uv, gl_ViewID_OVR)).r;
    if (AOIsFarDepth(centerDepth))
    {
        OutIntensity = 1.0f;
        return;
    }

    bool useNormals = UseNormalWeightedBlur && UseInputNormals;
    vec3 centerNormal = vec3(0.0f);
    if (useNormals)
        centerNormal = XRENGINE_ReadNormal(Normal, vec3(uv, gl_ViewID_OVR));

    float sigma = max(float(DenoiseRadius) * 0.5f, 1.0f);
    float sharpness = max(DenoiseSharpness, 0.001f);

    float result = 0.0f;
    float weightSum = 0.0f;

    int radius = clamp(DenoiseRadius, 1, 16);
    for (int offset = -radius; offset <= radius; ++offset)
    {
        vec2 sampleUV = clamp(uv + BlurDirection * texelSize * float(offset), vec2(0.0f), vec2(1.0f));
        float sampleAO = texture(GTAOInputTexture, vec3(sampleUV, gl_ViewID_OVR)).r;
        float sampleDepth = texture(DepthView, vec3(sampleUV, gl_ViewID_OVR)).r;
        if (AOIsFarDepth(sampleDepth))
            continue;

        float spatialWeight = exp(-0.5f * float(offset * offset) / (sigma * sigma));
        float depthWeight = exp(-abs(sampleDepth - centerDepth) * (24.0f * sharpness));

        float normalWeight = 1.0f;
        if (useNormals)
        {
            vec3 sampleNormal = XRENGINE_ReadNormal(Normal, vec3(sampleUV, gl_ViewID_OVR));
            normalWeight = pow(max(dot(sampleNormal, centerNormal), 0.0f), 1.0f + sharpness * 2.0f);
        }

        float weight = spatialWeight * depthWeight * normalWeight;
        result += sampleAO * weight;
        weightSum += weight;
    }

    OutIntensity = weightSum > 0.0f ? clamp(result / weightSum, 0.0f, 1.0f) : centerAO;
}