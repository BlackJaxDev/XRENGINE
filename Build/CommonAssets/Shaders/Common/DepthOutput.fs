#version 450
layout(location = 0) out float Depth;
void main()
{
    Depth = gl_FragCoord.z;
}
