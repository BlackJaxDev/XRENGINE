#version 460

layout (location = 0) out vec4 OutColor;
layout (location = 0) in vec4 MatColor;

void main()
{
    OutColor = MatColor;
}