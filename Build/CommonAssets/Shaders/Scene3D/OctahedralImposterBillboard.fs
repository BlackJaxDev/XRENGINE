#version 450 core

#include "../Common/OctahedralImposter.glsl"

layout(location = 0) out vec4 OutColor;
layout(location = 4) in vec2 FragUV0;

layout(binding = 0) uniform sampler2DArray ImposterViews;

uniform mat4 ModelMatrix;
uniform mat4 InverseViewMatrix;

void main()
{
    vec2 uv = clamp(FragUV0, 0.0, 1.0);
    vec3 viewDir = XR_OctahedralViewDirection(ModelMatrix, InverseViewMatrix);
    OutColor = XR_SampleOctahedralImposter(ImposterViews, uv, viewDir);
}
