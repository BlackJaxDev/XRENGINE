#version 450
#ifdef XRENGINE_DEPTH_NORMAL_PREPASS
layout (location = 0) out vec2 Normal;
#else
layout (location = 0) out vec4 OutColor;
#endif
layout (location = 1) in vec3 FragNorm;
uniform vec4 MatColor;

#pragma snippet "NormalEncoding"

void main()
{
#ifdef XRENGINE_DEPTH_NORMAL_PREPASS
	Normal = XRENGINE_EncodeNormal(FragNorm);
#else
	OutColor = MatColor;
#endif
}