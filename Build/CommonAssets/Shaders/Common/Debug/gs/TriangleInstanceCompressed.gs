#version 460

layout(points) in;
layout(triangle_strip, max_vertices = 4) out;

layout(location = 0) in int instanceID[];
layout(location = 0) flat out vec4 MatColor;

// Compressed layout: 10 floats per triangle (pos0.xyz, pos1.xyz, pos2.xyz, packed RGBA8 color)
layout(std430, binding = 0) buffer TrianglesBuffer
{
    float TriData[];
};

#include "../helper/DebugPerVertex.glsl"

uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;

#include "../helper/DebugTriangle.glsl"

void main()
{
    mat4 viewProj = ProjMatrix * inverse(InverseViewMatrix);

    int idx = instanceID[0] * 10;
    vec3 p0 = vec3(TriData[idx],     TriData[idx + 1], TriData[idx + 2]);
    vec3 p1 = vec3(TriData[idx + 3], TriData[idx + 4], TriData[idx + 5]);
    vec3 p2 = vec3(TriData[idx + 6], TriData[idx + 7], TriData[idx + 8]);
    vec4 color = unpackUnorm4x8(floatBitsToUint(TriData[idx + 9]));

    EmitTriangle(viewProj, p0, p1, p2, color);
}
