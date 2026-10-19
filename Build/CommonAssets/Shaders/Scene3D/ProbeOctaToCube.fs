#version 450
#pragma snippet "ProbeCubeFace"
#pragma snippet "OctahedralMapping"
layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;
uniform sampler2D Texture0;
uniform int Face;

void main()
{
    vec3 direction = XR_ProbeFaceDirection(Face, XR_ProbeRasterFacePlane(FragPos.xy));
    OutColor = vec4(textureLod(Texture0, XRENGINE_EncodeOcta(direction), 0.0).rgb, 1.0);
}
