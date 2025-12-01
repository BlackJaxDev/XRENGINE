#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 0) in vec3 FragPos;

uniform sampler2D Texture0;

const float PI = 3.14159265359f;

vec2 EncodeOcta(vec3 dir)
{
    dir = normalize(dir);
    vec3 octDir = vec3(dir.x, dir.z, dir.y);
    octDir /= max(abs(octDir.x) + abs(octDir.y) + abs(octDir.z), 1e-5f);

    vec2 uv = octDir.xy;
    if (octDir.z < 0.0f)
    {
        vec2 signDir = vec2(uv.x >= 0.0f ? 1.0f : -1.0f, uv.y >= 0.0f ? 1.0f : -1.0f);
        uv = (1.0f - abs(uv.yx)) * signDir;
    }

    return uv * 0.5f + 0.5f;
}

vec3 DecodeOcta(vec2 uv)
{
    vec2 f = uv * 2.0f - 1.0f;
    vec3 n = vec3(f.x, f.y, 1.0f - abs(f.x) - abs(f.y));

    if (n.z < 0.0f)
    {
        vec2 nXY = n.xy;
        vec2 signDir = vec2(nXY.x >= 0.0f ? 1.0f : -1.0f, nXY.y >= 0.0f ? 1.0f : -1.0f);
        n.xy = (1.0f - abs(nXY.yx)) * signDir;
    }

    vec3 dir = vec3(n.x, n.z, n.y);
    return normalize(dir);
}

vec3 DirectionFromFragPos(vec3 fragPos)
{
    vec2 clipXY = fragPos.xy;
    if (clipXY.x < -1.0f || clipXY.x > 1.0f || clipXY.y < -1.0f || clipXY.y > 1.0f)
        discard;

    vec2 uv = clipXY * 0.5f + 0.5f;
    return DecodeOcta(uv);
}

vec3 SampleOcta(sampler2D tex, vec3 dir)
{
    vec2 uv = EncodeOcta(dir);
    return texture(tex, uv).rgb;
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

            vec3 sampleVec = tanX * right + tanY * up + tanZ * N;

            irradiance += SampleOcta(Texture0, sampleVec) * cos(theta) * sin(theta);
            ++numSamples;
        }
    }

    OutColor = irradiance * vec3(PI / float(numSamples));
}
