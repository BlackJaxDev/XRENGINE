#version 450

#pragma snippet "OctahedralMapping"

layout(location = 0) out vec4 OutColor;
layout(location = 1) in vec3 FragNorm;

uniform sampler2D Texture0;

vec3 SafeNormalize(vec3 value, vec3 fallback)
{
    float len2 = dot(value, value);
    return len2 > 1e-10f ? value * inversesqrt(len2) : fallback;
}

void main()
{
    vec3 direction = SafeNormalize(FragNorm, vec3(0.0f, 1.0f, 0.0f));
    OutColor = vec4(XRENGINE_SampleOctaLod(Texture0, direction, 0.0f), 1.0f);
}
