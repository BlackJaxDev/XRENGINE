#version 450

layout (location = 0) in vec3 FragPos;
layout(location = 0) out float Depth;

uniform vec3 LightPos;
uniform float FarPlaneDist;

void main()
{
	// Store linear light distance in the color target for manual shadow comparison.
	// Do not override gl_FragDepth: the fixed-function depth test must use the
	// cubemap face projection depth, not radial distance, or nearest-surface
	// selection becomes incorrect and produces distorted point-light shadows.
	float d = length(FragPos - LightPos) / FarPlaneDist;
	Depth = d;
}
