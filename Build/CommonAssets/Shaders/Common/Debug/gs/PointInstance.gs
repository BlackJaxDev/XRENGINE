#version 460

layout(points) in;
layout(triangle_strip, max_vertices = 4) out;

layout(location = 0) in int instanceID[];
layout(location = 0) flat out vec4 MatColor;
layout(location = 1) out vec2 FragUV;

struct Point
{
    vec4 position;
    vec4 color;
};

layout(std430, binding = 0) buffer PointsBuffer
{
    Point Points[];
};

#include "Common/Debug/helper/DebugPerVertex.glsl"

uniform mat4 ViewProjectionMatrix;
uniform mat4 InverseViewMatrix;
uniform float PointSize = 0.01f;

#include "Common/Debug/helper/DebugPointQuad.glsl"

void main()
{
    int index = instanceID[0];
    Point point = Points[index];

    EmitPointQuad(ViewProjectionMatrix, point.position.xyz, point.color);
}