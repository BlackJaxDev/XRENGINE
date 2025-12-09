#version 450

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SSAOIntensityTexture; // Raw AO
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

    vec2 texelSize = 1.0f / vec2(textureSize(SSAOIntensityTexture, 0));

    float centerAO = texture(SSAOIntensityTexture, uv).r;
    float centerDepth = texture(DepthView, uv).r;
    vec3 centerNormal = normalize(texture(Normal, uv).rgb);

    float weightSum = 0.0f;
    float result = 0.0f;

    for (int x = -2; x <= 2; ++x)
    {
        for (int y = -2; y <= 2; ++y)
        {
            vec2 pixelOffset = vec2(float(x), float(y));
            vec2 sampleUV = uv + pixelOffset * texelSize;

            float sampleAO = texture(SSAOIntensityTexture, sampleUV).r;
            float sampleDepth = texture(DepthView, sampleUV).r;
            vec3 sampleNormal = normalize(texture(Normal, sampleUV).rgb);

            float depthWeight = exp(-abs(sampleDepth - centerDepth) * DepthPhi);
            float normalWeight = pow(max(dot(sampleNormal, centerNormal), 0.0f), NormalPhi);
            float spatialWeight = exp(-dot(pixelOffset, pixelOffset) * 0.35f);

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
