#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D HDRSceneTex;

void main()
{
    vec2 uv = FragPos.xy * 0.5 + 0.5;
    OutColor = texture(HDRSceneTex, uv);
}
