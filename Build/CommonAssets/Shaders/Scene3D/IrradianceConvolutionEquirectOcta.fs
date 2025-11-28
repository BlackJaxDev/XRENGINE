#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 0) in vec3 FragPos;

uniform sampler2D Texture0;

const float PI = 3.14159265359f;

vec3 DirectionFromFragPos(vec3 fragPos)
{
    vec2 clipXY = fragPos.xy;
    if (clipXY.x < -1.0f || clipXY.x > 1.0f || clipXY.y < -1.0f || clipXY.y > 1.0f)
        discard;

    vec2 uv = clipXY * 0.5f + 0.5f;

    vec2 f = uv * 2.0f - 1.0f;
    vec3 n = vec3(f.x, f.y, 1.0f - abs(f.x) - abs(f.y));
    if (n.z < 0.0f)
    {
        vec2 sign = vec2(f.x >= 0.0f ? 1.0f : -1.0f, f.y >= 0.0f ? 1.0f : -1.0f);
        n.x = sign.x * (1.0f - abs(n.y));
        n.y = sign.y * (1.0f - abs(n.x));
    }

    return normalize(n);
}

void main()
{
    vec3 N = DirectionFromFragPos(FragPos);

    vec3 irradiance = vec3(0.0f);
    vec3 up    = vec3(0.0f, 1.0f, 0.0f);
    vec3 right = cross(up, N);
    up         = cross(N, right);

    float sampleDelta = 0.025f;
    int numSamples = 0;
    for (float phi = 0.0f; phi < 2.0f * PI; phi += sampleDelta)
    {
        for (float theta = 0.0f; theta < 0.5f * PI; theta += sampleDelta)
        {
            float tanX = sin(theta) * cos(phi);
            float tanY = sin(theta) * sin(phi);
            float tanZ = cos(theta);

            vec2 uv = vec2((phi / (2.0f * PI)) + 0.5f, 1.0f - ((theta / PI) + 0.5f));
            vec3 sampleVec = tanX * right + tanY * up + tanZ * N;

            irradiance += texture(Texture0, uv).rgb * cos(theta) * sin(theta);
            ++numSamples;
        }
    }

    OutColor = irradiance * vec3(PI / float(numSamples));
}
