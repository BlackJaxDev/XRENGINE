#version 450

#pragma snippet "OctahedralMapping"

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform mat4 ModelMatrix;

uniform sampler2D Texture0;

void main()
{
    vec3 probeCenter = (ModelMatrix * vec4(0.0f, 0.0f, 0.0f, 1.0f)).xyz;
    vec3 direction = normalize(FragPos - probeCenter);
    OutColor = vec4(XRENGINE_SampleOctaLod(Texture0, direction, 0.0f), 1.0f);
}
