#version 450
#pragma snippet "ProbeCubeFace"
layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;
uniform samplerCube Texture0;
uniform int Face;

void main()
{
    OutColor = vec4(textureLod(Texture0,
        XR_ProbeFaceDirection(Face, XR_ProbeRasterFacePlane(FragPos.xy)), 0.0).rgb, 1.0);
}
