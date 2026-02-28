#version 460

layout(points) in;
layout(triangle_strip, max_vertices = 4) out;

layout(location = 0) in int instanceID[];
layout(location = 1) in vec3 vPos[];
layout(location = 0) out vec4 MatColor;

// Compressed layout: 7 floats per line (pos0.xyz, pos1.xyz, packed RGBA8 color)
layout(std430, binding = 0) buffer LinesBuffer
{
    float LineData[];
};

#include "Common/Debug/helper/DebugPerVertex.glsl"

uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;
uniform float LineWidth;
uniform int TotalLines;

uniform float ScreenWidth;
uniform float ScreenHeight;

#include "Common/Debug/helper/DebugLineQuad.glsl"

void main()
{
    mat4 viewProj = ProjMatrix * inverse(InverseViewMatrix);

    // Touch vPos to ensure Position attribute is kept alive across stages
    vec3 anchor = vPos[0];
    anchor *= 0.0f;

    int idx = instanceID[0] * 7;
    vec4 start = viewProj * vec4(vec3(LineData[idx], LineData[idx + 1], LineData[idx + 2]) + anchor, 1.0);
    vec4 end = viewProj * vec4(vec3(LineData[idx + 3], LineData[idx + 4], LineData[idx + 5]) + anchor, 1.0);
    vec4 color = unpackUnorm4x8(floatBitsToUint(LineData[idx + 6]));

    EmitLineQuad(start, end, color);
}
