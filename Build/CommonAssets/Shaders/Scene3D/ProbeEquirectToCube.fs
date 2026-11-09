#version 450
#pragma snippet "ProbeCubeFace"
layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;
uniform sampler2D Texture0;
uniform int Face;

void main()
{
    vec3 direction = normalize(XR_ProbeFaceDirection(Face, XR_ProbeRasterFacePlane(FragPos.xy)));
    vec2 uv = vec2(atan(direction.z, direction.x) / 6.28318530718 + 0.5,
        0.5 - asin(clamp(direction.y, -1.0, 1.0)) / 3.14159265359);
    OutColor = vec4(textureLod(Texture0, uv, 0.0).rgb, 1.0);
}
