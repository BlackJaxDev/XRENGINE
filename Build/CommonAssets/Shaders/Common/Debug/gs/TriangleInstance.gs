#version 460

layout(points) in;
layout(triangle_strip, max_vertices = 4) out;

layout(location = 0) in int instanceID[];
layout(location = 0) flat out vec4 MatColor;

struct Triangle
{
    vec4 p0;
    vec4 p1;
    vec4 p2;
    vec4 color;
};

layout(std430, binding = 0) buffer TrianglesBuffer
{
    Triangle Triangles[];
};

#include "Common/Debug/helper/DebugPerVertex.glsl"

uniform mat4 ViewProjectionMatrix;

#include "Common/Debug/helper/DebugTriangle.glsl"

void main()
{
    int index = instanceID[0];
    Triangle tri = Triangles[index];

    EmitTriangle(ViewProjectionMatrix, tri.p0.xyz, tri.p1.xyz, tri.p2.xyz, tri.color);
}