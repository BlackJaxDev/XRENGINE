#version 460

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D PpllFragmentCountTex;

vec3 Heatmap(float t)
{
    vec3 cold = vec3(0.05, 0.15, 0.45);
    vec3 warm = vec3(0.95, 0.65, 0.10);
    vec3 hot = vec3(1.0, 0.15, 0.05);
    if (t < 0.5)
        return mix(cold, warm, t * 2.0);
    return mix(warm, hot, (t - 0.5) * 2.0);
}

void main()
{
    vec2 uv = FragPos.xy * 0.5 + 0.5;
    float count = texture(PpllFragmentCountTex, uv).r;
    float normalized = clamp(count / 8.0, 0.0, 1.0);
    OutColor = vec4(Heatmap(normalized), 1.0);
}
