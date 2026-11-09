#version 460
#extension GL_OVR_multiview2 : require
#include "AOCommon.glsl"

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray AmbientOcclusionTexture;
uniform float AmbientOcclusionPower = 1.0f;

void main()
{
    if (FragPos.x > 1.0f || FragPos.y > 1.0f)
        discard;
    vec2 uv = AOTextureUVFromFragPos(FragPos);
    float ao = clamp(texture(AmbientOcclusionTexture, vec3(uv, gl_ViewID_OVR)).r, 0.0f, 1.0f);
    OutIntensity = pow(ao, max(AmbientOcclusionPower, 0.0001f));
}
