#version 460
#extension GL_OVR_multiview2 : require

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray TransparentSceneCopyTex;
uniform sampler2DArray TransparentAccumTex;
uniform sampler2DArray TransparentRevealageTex;

vec3 GetUv()
{
    return vec3(FragPos.xy * 0.5 + 0.5, gl_ViewID_OVR);
}

void main()
{
    vec3 uv = GetUv();
    vec4 sceneColor = texture(TransparentSceneCopyTex, uv);
    vec4 accum = texture(TransparentAccumTex, uv);
    float revealage = clamp(texture(TransparentRevealageTex, uv).r, 0.0, 1.0);
    float opacity = clamp(1.0 - revealage, 0.0, 1.0);
    vec3 transparentColor = accum.a > 1e-5 ? accum.rgb / accum.a : vec3(0.0);
    vec3 composite = mix(sceneColor.rgb, transparentColor, opacity);
    OutColor = vec4(composite, max(sceneColor.a, opacity));
}