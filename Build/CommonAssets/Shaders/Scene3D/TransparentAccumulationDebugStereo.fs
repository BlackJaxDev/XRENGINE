#version 460
#extension GL_OVR_multiview2 : require

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray TransparentAccumTex;

void main()
{
    vec4 accum = texture(TransparentAccumTex, vec3(FragPos.xy * 0.5 + 0.5, gl_ViewID_OVR));
    vec3 color = accum.a > 1e-5 ? accum.rgb / accum.a : vec3(0.0);
    OutColor = vec4(color, 1.0);
}