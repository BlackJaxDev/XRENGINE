#version 460

layout(location = 0) in vec3 FragPos;
layout(location = 20) in vec3 FragPosLocal;
layout(location = 22) in float FragViewIndex;
layout(location = 0) out vec2 OutVelocity;

uniform sampler2D Texture0;
uniform mat4 ReflectedViewProjection;
uniform bool FramebufferYDown;
uniform mat4 ModelMatrix;
uniform mat4 PrevModelMatrix;
uniform mat4 CurrViewProjection;
uniform mat4 PrevViewProjection;
uniform mat4 RightEyeCurrViewProjection;
uniform mat4 RightEyePrevViewProjection;
uniform bool LeftEyeHistoryReady;
uniform bool RightEyeHistoryReady;

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

    bool rightEye = FragViewIndex > 0.5;
    if (!(rightEye ? RightEyeHistoryReady : LeftEyeHistoryReady))
    {
        OutVelocity = vec2(0.0);
        return;
    }

    mat4 currentViewProjection = rightEye ? RightEyeCurrViewProjection : CurrViewProjection;
    mat4 previousViewProjection = rightEye ? RightEyePrevViewProjection : PrevViewProjection;
    vec4 current = currentViewProjection * ModelMatrix * vec4(FragPosLocal, 1.0);
    vec4 previous = previousViewProjection * PrevModelMatrix * vec4(FragPosLocal, 1.0);
    OutVelocity = abs(current.w) <= 1.0e-5 || abs(previous.w) <= 1.0e-5
        ? vec2(0.0)
        : clamp(current.xy / current.w - previous.xy / previous.w, vec2(-2.0), vec2(2.0));
}
