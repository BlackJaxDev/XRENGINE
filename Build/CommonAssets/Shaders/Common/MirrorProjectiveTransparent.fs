#version 460

layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;

uniform sampler2D Texture0;
uniform mat4 ReflectedViewProjection;
uniform bool FramebufferYDown;

void main()
{
    vec4 clip = ReflectedViewProjection * vec4(FragPos, 1.0);
    if (isnan(clip.x) || isnan(clip.y) || isnan(clip.z) || isnan(clip.w) ||
        isinf(clip.x) || isinf(clip.y) || isinf(clip.z) || isinf(clip.w) || clip.w <= 1.0e-6)
    {
        discard;
    }

    vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
    if (FramebufferYDown)
        uv.y = 1.0 - uv.y;
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
    {
        discard;
    }

    vec4 sampleColor = texture(Texture0, uv);
    if (isnan(sampleColor.a) || isinf(sampleColor.a) || sampleColor.a <= 0.001)
    {
        discard;
    }

    vec3 rgb = any(isnan(sampleColor.rgb)) || any(isinf(sampleColor.rgb))
        ? vec3(0.0)
        : max(sampleColor.rgb, vec3(0.0));
    OutColor = vec4(rgb, clamp(sampleColor.a, 0.0, 1.0));
}
