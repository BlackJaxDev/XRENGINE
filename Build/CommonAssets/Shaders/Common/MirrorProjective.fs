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
        OutColor = vec4(0.0);
        return;
    }

    vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
    if (FramebufferYDown)
        uv.y = 1.0 - uv.y;
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
    {
        OutColor = vec4(0.0);
        return;
    }

    OutColor = texture(Texture0, uv);
}
