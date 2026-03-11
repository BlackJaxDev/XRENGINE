#version 450

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

void main()
{
    vec2 uv = FragPos.xy;
    if (uv.x > 1.0f || uv.y > 1.0f)
        discard;

    OutIntensity = 1.0f;
}