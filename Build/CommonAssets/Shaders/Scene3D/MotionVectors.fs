#version 450 core

layout(location = 0) out vec2 OutVelocity;

layout(location = 20) in vec3 FragPosLocal;

uniform mat4 ModelMatrix;
uniform mat4 PrevModelMatrix;
uniform mat4 CurrViewProjection;
uniform mat4 PrevViewProjection;
uniform bool HistoryReady;

vec4 Project(mat4 vp, mat4 model, vec3 localPosition)
{
    return vp * (model * vec4(localPosition, 1.0f));
}

void main()
{
    if (!HistoryReady)
    {
        OutVelocity = vec2(0.0f);
        return;
    }

    vec4 currClip = Project(CurrViewProjection, ModelMatrix, FragPosLocal);
    vec4 prevClip = Project(PrevViewProjection, PrevModelMatrix, FragPosLocal);

    if (abs(currClip.w) <= 1e-5f || abs(prevClip.w) <= 1e-5f)
    {
        OutVelocity = vec2(0.0f);
        return;
    }

    vec2 currNdc = currClip.xy / currClip.w;
    vec2 prevNdc = prevClip.xy / prevClip.w;
    OutVelocity = currNdc - prevNdc;
}
