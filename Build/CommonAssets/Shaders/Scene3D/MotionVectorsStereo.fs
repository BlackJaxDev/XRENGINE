#version 460
#extension GL_OVR_multiview2 : require

layout(location = 0) out vec2 OutVelocity;

layout(location = 20) in vec3 FragPosLocal;

uniform mat4 ModelMatrix;
uniform mat4 PrevModelMatrix;
uniform mat4 CurrViewProjectionStereo[2];
uniform mat4 PrevViewProjectionStereo[2];

vec4 Project(mat4 vp, mat4 model, vec3 localPosition)
{
    return vp * (model * vec4(localPosition, 1.0));
}

void main()
{
    int eyeIndex = int(gl_ViewID_OVR);
    mat4 currViewProjection = CurrViewProjectionStereo[eyeIndex];
    mat4 prevViewProjection = PrevViewProjectionStereo[eyeIndex];

    vec4 currClip = Project(currViewProjection, ModelMatrix, FragPosLocal);
    vec4 prevClip = Project(prevViewProjection, PrevModelMatrix, FragPosLocal);

    if (abs(currClip.w) <= 1e-5 || abs(prevClip.w) <= 1e-5)
    {
        OutVelocity = vec2(0.0);
        return;
    }

    vec2 currNdc = currClip.xy / currClip.w;
    vec2 prevNdc = prevClip.xy / prevClip.w;
    OutVelocity = clamp(currNdc - prevNdc, vec2(-2.0), vec2(2.0));
}
