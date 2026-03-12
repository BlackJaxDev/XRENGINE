#version 460
#extension GL_OVR_multiview2 : require

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray TransparentRevealageTex;

void main()
{
    float revealage = texture(TransparentRevealageTex, vec3(FragPos.xy * 0.5 + 0.5, gl_ViewID_OVR)).r;
    OutColor = vec4(vec3(revealage), 1.0);
}