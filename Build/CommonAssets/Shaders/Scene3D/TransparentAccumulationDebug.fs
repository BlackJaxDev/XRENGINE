#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D TransparentAccumTex;

void main()
{
    vec2 uv = FragPos.xy * 0.5 + 0.5;
    vec4 accum = texture(TransparentAccumTex, uv);
    vec3 color = accum.a > 1e-5 ? accum.rgb / accum.a : vec3(0.0);
    OutColor = vec4(color, 1.0);
}