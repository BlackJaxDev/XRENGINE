#version 460
#extension GL_OVR_multiview2 : require
//#extension GL_EXT_multiview_tessellation_geometry_shader : enable
#include "AOCommon.glsl"

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;
uniform sampler2DArray AmbientOcclusionTexture;

void main()
{
    if (FragPos.x > 1.0f || FragPos.y > 1.0f)
        discard;
    vec2 uv = AOTextureUVFromFragPos(FragPos);
    
    vec2 texelSize = 1.0f / textureSize(AmbientOcclusionTexture, 0).xy;
    float result = 0.0f;
    for (int x = -2; x < 2; ++x) 
    {
        for (int y = -2; y < 2; ++y) 
        {
            vec2 offset = vec2(float(x), float(y)) * texelSize;
            result += texture(AmbientOcclusionTexture, vec3(uv + offset, gl_ViewID_OVR)).r;
        }
    }
    OutIntensity = result / 16.0f;
}
