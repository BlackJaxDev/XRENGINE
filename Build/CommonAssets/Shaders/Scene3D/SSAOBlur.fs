#version 450
#include "AOCommon.glsl"

layout(location = 0) out float OutIntensity;
layout(location = 0) in vec3 FragPos;
uniform sampler2D AmbientOcclusionTexture;

void main()
{
    if (FragPos.x > 1.0f || FragPos.y > 1.0f)
        discard;
    vec2 uv = AOTextureUVFromFragPos(FragPos);
    
    vec2 texelSize = 1.0f / vec2(textureSize(AmbientOcclusionTexture, 0));
    float result = 0.0f;
    for (int x = -2; x < 2; ++x) 
    {
        for (int y = -2; y < 2; ++y) 
        {
            vec2 offset = vec2(float(x), float(y)) * texelSize;
            result += texture(AmbientOcclusionTexture, uv + offset).r;
        }
    }
    OutIntensity = result / 16.0f;
}
