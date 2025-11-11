#version 450

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2D Texture0; // Raw AO
uniform sampler2D Texture1; // Depth
uniform sampler2D Texture2; // Normal

uniform float DepthPhi = 4.0f;
uniform float NormalPhi = 64.0f;

void main()
{
    vec2 uv = FragPos.xy;
    if (uv.x > 1.0f || uv.y > 1.0f)
        discard;
    uv = uv * 0.5f + 0.5f;

    vec2 texelSize = 1.0f / vec2(textureSize(Texture0, 0));

    float centerAO = texture(Texture0, uv).r;
    float centerDepth = texture(Texture1, uv).r;
    vec3 centerNormal = normalize(texture(Texture2, uv).rgb);

    float weightSum = 0.0f;
    float result = 0.0f;

    for (int x = -2; x <= 2; ++x)
    {
        for (int y = -2; y <= 2; ++y)
        {
            vec2 pixelOffset = vec2(float(x), float(y));
            vec2 sampleUV = uv + pixelOffset * texelSize;

            float sampleAO = texture(Texture0, sampleUV).r;
            float sampleDepth = texture(Texture1, sampleUV).r;
            vec3 sampleNormal = normalize(texture(Texture2, sampleUV).rgb);

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
