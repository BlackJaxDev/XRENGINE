#version 460

layout(points) in;
layout(triangle_strip, max_vertices = 4) out;

layout(location = 0) in int instanceID[];
layout(location = 1) in vec3 vPos[];
layout(location = 0) flat out vec4 LineMatColor;
layout(location = 1) noperspective out float LineEdgeCoord;
layout(location = 2) flat out float LineHalfWidthPixels;

layout(std430, binding = 0) buffer LinesBuffer
{
    vec4 Lines[];
};

#include "Common/Debug/helper/DebugPerVertex.glsl"

uniform mat4 ViewProjectionMatrix;
uniform float LineWidth;
uniform int TotalLines;

uniform float ScreenWidth;
uniform float ScreenHeight;

#include "Common/Debug/helper/DebugLineQuad.glsl"

void main()
{
    // Touch vPos to ensure Position attribute is kept alive across stages
    vec3 anchor = vPos[0];
    anchor *= 0.0f;

    int index = instanceID[0] * 3;
    vec4 start = ViewProjectionMatrix * vec4(Lines[index].xyz + anchor, 1.0);
    vec4 end = ViewProjectionMatrix * vec4(Lines[index + 1].xyz + anchor, 1.0);
    vec4 color = Lines[index + 2];

    EmitLineQuad(start, end, color);
}