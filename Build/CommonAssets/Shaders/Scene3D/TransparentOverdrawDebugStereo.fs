#version 460
#extension GL_OVR_multiview2 : require

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray TransparentRevealageTex;
uniform sampler2DArray TransparentAccumTex;

vec3 Heat(float t)
{
    vec3 cool = vec3(0.05, 0.2, 0.7);
    vec3 warm = vec3(1.0, 0.85, 0.1);
    vec3 hot = vec3(1.0, 0.2, 0.05);
    return t < 0.5 ? mix(cool, warm, t * 2.0) : mix(warm, hot, (t - 0.5) * 2.0);
}

void main()
{
    vec3 uv = vec3(FragPos.xy * 0.5 + 0.5, gl_ViewID_OVR);
    float revealage = clamp(texture(TransparentRevealageTex, uv).r, 0.0, 1.0);
    float opacity = 1.0 - revealage;
    float weight = texture(TransparentAccumTex, uv).a;
    float heat = clamp(opacity * 4.0 + weight * 0.05, 0.0, 1.0);
    OutColor = vec4(Heat(heat), 1.0);
}