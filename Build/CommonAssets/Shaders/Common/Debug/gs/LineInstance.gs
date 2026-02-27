#version 460

layout(points) in;
layout(triangle_strip, max_vertices = 4) out;

layout(location = 0) in int instanceID[];
layout(location = 1) in vec3 vPos[];
layout(location = 0) out vec4 MatColor;

layout(std430, binding = 0) buffer LinesBuffer
{
    vec4 Lines[];
};

#include "../helper/DebugPerVertex.glsl"

uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;
uniform float LineWidth;
uniform int TotalLines;

uniform float ScreenWidth;
uniform float ScreenHeight;

#include "../helper/DebugLineQuad.glsl"

void main()
{
    mat4 viewProj = ProjMatrix * inverse(InverseViewMatrix);

    // Touch vPos to ensure Position attribute is kept alive across stages
    vec3 anchor = vPos[0];
    anchor *= 0.0f;

    int index = instanceID[0] * 3;
    vec4 start = viewProj * vec4(Lines[index].xyz + anchor, 1.0);
    vec4 end = viewProj * vec4(Lines[index + 1].xyz + anchor, 1.0);
    vec4 color = Lines[index + 2];

    EmitLineQuad(start, end, color);
}