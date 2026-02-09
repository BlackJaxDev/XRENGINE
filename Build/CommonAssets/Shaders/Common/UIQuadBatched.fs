#version 460

layout (location = 4) in vec2 FragUV0;
flat in vec4 InstanceColor;

layout (location = 0) out vec4 OutColor;

void main()
{
    OutColor = InstanceColor;
}
