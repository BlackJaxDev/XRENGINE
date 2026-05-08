#version 450

#pragma snippet "ShadowMomentEncoding"

layout (location = 0) in vec3 FragPos;
layout(location = 0) out vec4 ShadowDepth;

uniform vec3 LightPos;
uniform float FarPlaneDist;

void main()
{
	// Store linear light distance in the color target for manual shadow comparison.
	// Do not override gl_FragDepth: the fixed-function depth test must use the
	// cubemap face projection depth, not radial distance, or nearest-surface
	// selection becomes incorrect and produces distorted point-light shadows.
	float d = clamp(length(FragPos - LightPos) / max(FarPlaneDist, 0.0001), 0.0, 1.0);
	ShadowDepth = XRENGINE_EncodeShadowMoments(
		ShadowMapEncoding,
		d,
		ShadowMomentPositiveExponent,
		ShadowMomentNegativeExponent,
		false);
}
