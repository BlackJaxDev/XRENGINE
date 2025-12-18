#version 450

layout (location = 0) out vec4 OutColor;
layout (location = 4) in vec2 FragUV0;

uniform sampler2D Texture0;
uniform float AlphaThreshold = 0.05f;

void main()
{
    vec4 color = texture(Texture0, FragUV0);
    if (color.a < AlphaThreshold)
      discard;
    OutColor = color / 2.0f;
}
