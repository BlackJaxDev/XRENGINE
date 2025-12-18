#version 450

layout (location = 0) out vec4 OutColor;
layout (location = 0) in vec3 FragPos;
layout (location = 4) in vec2 FragUV0;

uniform vec3 Color = vec3(1.0f);
uniform vec2 Size;
uniform float LineWidth = 5.0f;

void main()
{
    vec2 dist = abs(FragUV0 - vec2(0.5f)) * 2.0f; // 0 to 1 from center
    vec2 px = 1.0f / Size;
    vec2 overOne = floor(dist + px * LineWidth);
    float max =  clamp(overOne.x + overOne.y, 0.0f, 1.0f);
    if (max < 1.0f)
      discard;
    OutColor = vec4(Color, 1.0f);
}
