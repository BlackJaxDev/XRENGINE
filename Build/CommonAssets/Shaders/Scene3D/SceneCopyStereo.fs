#version 460
#extension GL_OVR_multiview2 : require

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray HDRSceneTex;

void main()
{
    vec3 uv = vec3(XRENGINE_ClipXYToFramebufferTextureUV(FragPos.xy), gl_ViewID_OVR);
    OutColor = texture(HDRSceneTex, uv);
}