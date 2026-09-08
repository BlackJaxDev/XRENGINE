#version 460
layout(location = 0) out float OutReactiveMask;
uniform vec4 MatColor;
void main()
{
    if (MatColor.a <= 0.0) discard;
    OutReactiveMask = 1.0;
}
