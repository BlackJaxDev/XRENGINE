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
    // Early out if we don't have valid history data
    if (!HistoryReady)
    {
        OutVelocity = vec2(0.0f);
        return;
    }

    vec4 currClip = Project(CurrViewProjection, ModelMatrix, FragPosLocal);
    vec4 prevClip = Project(PrevViewProjection, PrevModelMatrix, FragPosLocal);

    // Protect against w near zero (behind camera or at infinity)
    if (abs(currClip.w) <= 1e-5f || abs(prevClip.w) <= 1e-5f)
    {
        OutVelocity = vec2(0.0f);
        return;
    }

    vec2 currNdc = currClip.xy / currClip.w;
    vec2 prevNdc = prevClip.xy / prevClip.w;
    
    // Calculate velocity (current - previous position in NDC)
    vec2 velocity = currNdc - prevNdc;
    
    // Clamp to reasonable values to prevent artifacts from extreme motion
    // NDC is -1 to 1, so max reasonable motion per frame is ~2 (full screen)
    velocity = clamp(velocity, vec2(-2.0), vec2(2.0));
    
    OutVelocity = velocity;
}
