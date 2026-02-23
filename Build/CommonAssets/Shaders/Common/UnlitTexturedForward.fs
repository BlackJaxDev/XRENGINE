#version 460

layout(location = 0) out vec4 OutColor;

layout (location = 4) in vec2 FragUV0;

uniform sampler2D Texture0;

void main()
{
    OutColor = texture(Texture0, FragUV0);
}