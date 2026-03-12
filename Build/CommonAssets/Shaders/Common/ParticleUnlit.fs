#version 450

layout (location = 0) out vec4 OutColor;
layout (location = 0) in vec3 FragPos;
layout (location = 4) in vec4 FragColor0;

void main()
{
    OutColor = FragColor0;
}