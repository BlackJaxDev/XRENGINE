#version 450

layout (location = 0) out vec4 OutColor;
layout (location = 4) in vec2 FragUV0;

uniform sampler2D Texture0;
uniform float AlphaCutoff = 0.1f;

#pragma snippet "ExactTransparencyDepthPeel"

void main()
{
    if (XRE_ShouldDiscardDepthPeelFragment())
        discard;

    vec4 color = texture(Texture0, FragUV0);
    if (color.a < AlphaCutoff)
        discard;
    OutColor = color;
}