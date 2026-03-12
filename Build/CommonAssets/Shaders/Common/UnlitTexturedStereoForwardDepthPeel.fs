#version 460
#extension GL_OVR_multiview2 : require

layout (location = 0) out vec4 OutColor;
layout (location = 4) in vec2 FragUV0;

uniform sampler2DArray Texture0;

#pragma snippet "ExactTransparencyDepthPeel"

void main()
{
    if (XRE_ShouldDiscardDepthPeelFragment())
        discard;
    OutColor = texture(Texture0, vec3(FragUV0, gl_ViewID_OVR));
}