#version 460
#extension GL_OVR_multiview2 : require

#ifdef XRENGINE_DEPTH_NORMAL_PREPASS
layout (location = 0) out vec2 Normal;
#else
layout(location = 0) out vec4 OutColor;
#endif

layout (location = 1) in vec3 FragNorm;
layout (location = 4) in vec2 FragUV0;

uniform sampler2DArray Texture0;

#pragma snippet "NormalEncoding"

void main()
{
#ifdef XRENGINE_DEPTH_NORMAL_PREPASS
    Normal = XRENGINE_EncodeNormal(normalize(FragNorm));
#else
    vec2 uv = FragUV0;
    vec3 uvi = vec3(uv, gl_ViewID_OVR);
    OutColor = texture(Texture0, uvi);
#endif
}
