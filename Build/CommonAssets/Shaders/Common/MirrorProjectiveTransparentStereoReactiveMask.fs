#version 460

layout(location = 0) in vec3 FragPos;
layout(location = 22) in float FragViewIndex;
layout(location = 0) out float OutReactiveMask;

uniform sampler2D Texture0;
uniform sampler2D Texture1;
uniform mat4 ReflectedViewProjection0;
uniform mat4 ReflectedViewProjection1;
uniform bool FramebufferYDown;

void main()
{
    bool rightEye = FragViewIndex > 0.5;
    mat4 viewProj = rightEye ? ReflectedViewProjection1 : ReflectedViewProjection0;
    vec4 clip = viewProj * vec4(FragPos, 1.0);
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

    vec4 sampleColor = rightEye ? texture(Texture1, uv) : texture(Texture0, uv);
    if (isnan(sampleColor.a) || isinf(sampleColor.a) || sampleColor.a <= 0.001)
    {
        discard;
    }

    OutReactiveMask = 1.0;
}
