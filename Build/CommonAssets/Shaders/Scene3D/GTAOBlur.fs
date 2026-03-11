#version 450

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2D GTAOInputTexture;
uniform sampler2D DepthView;
uniform sampler2D Normal;

uniform vec2 BlurDirection = vec2(1.0f, 0.0f);
uniform int DenoiseRadius = 4;
uniform float DenoiseSharpness = 4.0f;
uniform bool DenoiseEnabled = true;
uniform bool UseInputNormals = true;

void main()
{
    vec2 uv = FragPos.xy;
    if (uv.x > 1.0f || uv.y > 1.0f)
        discard;
    uv = uv * 0.5f + 0.5f;

    float centerAO = texture(GTAOInputTexture, uv).r;
    if (!DenoiseEnabled || DenoiseRadius <= 0)
    {
        OutIntensity = centerAO;
        return;
    }

    vec2 texelSize = 1.0f / vec2(textureSize(GTAOInputTexture, 0));
    float centerDepth = texture(DepthView, uv).r;
    vec3 centerNormal = normalize(texture(Normal, uv).rgb);
    float sigma = max(float(DenoiseRadius) * 0.5f, 1.0f);
    float sharpness = max(DenoiseSharpness, 0.001f);

    float result = 0.0f;
    float weightSum = 0.0f;

    for (int offset = -16; offset <= 16; ++offset)
    {
        if (abs(offset) > DenoiseRadius)
            continue;

        vec2 sampleUV = clamp(uv + BlurDirection * texelSize * float(offset), vec2(0.0f), vec2(1.0f));
        float sampleAO = texture(GTAOInputTexture, sampleUV).r;
        float sampleDepth = texture(DepthView, sampleUV).r;
        vec3 sampleNormal = normalize(texture(Normal, sampleUV).rgb);

        float spatialWeight = exp(-0.5f * float(offset * offset) / (sigma * sigma));
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