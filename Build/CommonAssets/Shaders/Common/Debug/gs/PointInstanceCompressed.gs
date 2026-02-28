#version 460

layout(points) in;
layout(triangle_strip, max_vertices = 4) out;

layout(location = 0) in int instanceID[];
layout(location = 0) flat out vec4 MatColor;
layout(location = 1) out vec2 FragUV;

// Compressed layout: 4 floats per point (pos.xyz + packed RGBA8 color)
layout(std430, binding = 0) buffer PointsBuffer
{
    float PointData[];
};

#include "Common/Debug/helper/DebugPerVertex.glsl"

uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;
uniform float PointSize = 0.01f;

#include "Common/Debug/helper/DebugPointQuad.glsl"

void main()
{
    mat4 viewProj = ProjMatrix * inverse(InverseViewMatrix);

    int idx = instanceID[0] * 4;
    vec3 center = vec3(PointData[idx], PointData[idx + 1], PointData[idx + 2]);
    vec4 color = unpackUnorm4x8(floatBitsToUint(PointData[idx + 3]));

    EmitPointQuad(viewProj, center, color);
}
