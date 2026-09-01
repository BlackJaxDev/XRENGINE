#version 450

layout(location = 0) in vec3 Position;

layout(location = 0) out vec3 NearWorldPos;
layout(location = 1) out vec3 FarWorldPos;
layout(location = 2) out vec2 FragClipXY;

uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform int DepthMode;
uniform int ClipDepthRange;

vec3 Unproject(vec2 clipXY, float clipZ, mat4 invView, mat4 invProj)
{
    vec4 viewPos = invProj * vec4(clipXY, clipZ, 1.0);
    float invW = abs(viewPos.w) > 1e-6 ? 1.0 / viewPos.w : 1.0;
    return (invView * (viewPos * invW)).xyz;
}

float GetNearClipZ()
{
    if (DepthMode == 1) // Reverse-Z
        return 1.0;
    return ClipDepthRange == 1 ? -1.0 : 0.0;
}

float GetFarClipZ()
{
    if (DepthMode == 1) // Reverse-Z
        return ClipDepthRange == 1 ? -1.0 : 0.0;
    return 1.0;
}

void main()
{
    vec2 clipXY = Position.xy;
    FragClipXY = clipXY;

    NearWorldPos = Unproject(clipXY, GetNearClipZ(), InverseViewMatrix, InverseProjMatrix);
    FarWorldPos = Unproject(clipXY, GetFarClipZ(), InverseViewMatrix, InverseProjMatrix);

    gl_Position = vec4(clipXY, GetNearClipZ(), 1.0);
}
