#version 450

layout (location = 0) out vec4 OutColor;
uniform vec4 MatColor;

#pragma snippet "ExactTransparencyDepthPeel"

void main()
{
    if (XRE_ShouldDiscardDepthPeelFragment())
        discard;
    OutColor = MatColor;
}