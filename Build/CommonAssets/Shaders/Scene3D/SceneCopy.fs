#version 450

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D HDRSceneTex;

void main()
{
    vec2 uv = XRENGINE_ClipXYToFramebufferTextureUV(FragPos.xy);
    OutColor = texture(HDRSceneTex, uv);
}
