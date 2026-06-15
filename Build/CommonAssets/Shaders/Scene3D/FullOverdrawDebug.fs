#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D FullOverdrawCountTex;
uniform sampler2D PostProcessOutputTexture;
uniform float OverdrawMaxCount = 8.0;
uniform float OverlayOpacity = 1.0;
uniform int FramebufferTextureYDirection;

vec3 Heat(float t)
{
    vec3 cold = vec3(0.02, 0.14, 0.55);
    vec3 cool = vec3(0.00, 0.65, 0.95);
    vec3 mid = vec3(0.10, 0.85, 0.25);
    vec3 warm = vec3(1.00, 0.85, 0.05);
    vec3 hot = vec3(1.00, 0.08, 0.02);

    if (t < 0.25)
        return mix(cold, cool, t * 4.0);
    if (t < 0.50)
        return mix(cool, mid, (t - 0.25) * 4.0);
    if (t < 0.75)
        return mix(mid, warm, (t - 0.50) * 4.0);
    return mix(warm, hot, (t - 0.75) * 4.0);
}

vec2 ResolveFramebufferTextureUv(vec2 uv)
{
    if (FramebufferTextureYDirection == 1)
        uv.y = 1.0 - uv.y;
    return uv;
}

void main()
{
    vec2 uv = ResolveFramebufferTextureUv(FragPos.xy * 0.5 + 0.5);
    float count = texture(FullOverdrawCountTex, uv).r;
    vec3 scene = texture(PostProcessOutputTexture, uv).rgb;

    float maxCount = max(OverdrawMaxCount, 1.0);
    float heatValue = clamp((count - 1.0) / max(maxCount - 1.0, 1.0), 0.0, 1.0);
    float drawn = step(0.5, count);
    float overlay = clamp(OverlayOpacity, 0.0, 1.0) * drawn;

    OutColor = vec4(mix(scene, Heat(heatValue), overlay), 1.0);
}
