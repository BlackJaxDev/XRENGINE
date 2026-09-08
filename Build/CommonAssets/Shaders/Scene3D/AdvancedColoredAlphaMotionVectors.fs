#version 460
layout(location = 0) out vec2 OutVelocity;
layout(location = 20) in vec3 FragPosLocal;
layout(location = 22) in float FragViewIndex;
uniform vec4 MatColor;
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
    if (MatColor.a <= 0.0) discard;
    bool rightEye = FragViewIndex > 0.5;
    // A reset invalidates object history as well as camera history.
    if (!(rightEye ? RightEyeHistoryReady : LeftEyeHistoryReady))
    {
        OutVelocity = vec2(0.0);
        return;
    }
    mat4 currentViewProjection = rightEye ? RightEyeCurrViewProjection : CurrViewProjection;
    mat4 previousViewProjection = rightEye ? RightEyePrevViewProjection : PrevViewProjection;
    vec4 current = currentViewProjection * ModelMatrix * vec4(FragPosLocal, 1.0);
    vec4 previous = previousViewProjection * PrevModelMatrix * vec4(FragPosLocal, 1.0);
    OutVelocity = abs(current.w) <= 1e-5 || abs(previous.w) <= 1e-5 ? vec2(0.0) : clamp(current.xy / current.w - previous.xy / previous.w, vec2(-2.0), vec2(2.0));
}
