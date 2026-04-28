#version 450

#pragma snippet "OctahedralMapping"

layout (location = 0) out vec3 OutColor;
layout (location = 0) in vec3 FragPos;

uniform samplerCube Texture0;

const float PI = 3.14159265359f;

vec3 DirectionFromFragPos(vec3 fragPos)
{
    vec2 clipXY = fragPos.xy;
    if (clipXY.x < -1.0f || clipXY.x > 1.0f || clipXY.y < -1.0f || clipXY.y > 1.0f)
        discard;

    vec2 uv = clipXY * 0.5f + 0.5f;
    return XRENGINE_DecodeOcta(uv);
}

void main()
{
    vec3 N = DirectionFromFragPos(FragPos);

    vec3 irradiance = vec3(0.0f);

    vec3 up    = abs(N.y) < 0.999f ? vec3(0.0f, 1.0f, 0.0f) : vec3(0.0f, 0.0f, 1.0f);
    vec3 right = normalize(cross(up, N));
    up         = normalize(cross(N, right));

    float sampleDelta = 0.025f;
    int numSamples = 0;
    for (float phi = 0.0f; phi < 2.0f * PI; phi += sampleDelta)
    {
        for (float theta = 0.0f; theta < 0.5f * PI; theta += sampleDelta)
        {
            float tanX = sin(theta) * cos(phi);
            float tanY = sin(theta) * sin(phi);
            float tanZ = cos(theta);

            vec3 sampleVec = tanX * right + tanY * up + tanZ * N;

            irradiance += texture(Texture0, sampleVec).rgb * cos(theta) * sin(theta);
            ++numSamples;
        }
    }

    OutColor = irradiance * vec3(PI / float(numSamples));
}