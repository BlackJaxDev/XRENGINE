#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 20) in vec3 FragPosLocal;

uniform samplerCube Texture0;

const float PI = 3.14159265359f;

void main()
{
    vec3 N = normalize(FragPosLocal);

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
            // spherical to cartesian (in tangent space)
            float tanX = sin(theta) * cos(phi);
            float tanY = sin(theta) * sin(phi);
            float tanZ = cos(theta);

            // tangent space to world
            vec3 sampleVec = tanX * right + tanY * up + tanZ * N;

            irradiance += texture(Texture0, sampleVec).rgb * cos(theta) * sin(theta);
            ++numSamples;
        }
    }

    OutColor = irradiance * vec3(PI / float(numSamples));
}
