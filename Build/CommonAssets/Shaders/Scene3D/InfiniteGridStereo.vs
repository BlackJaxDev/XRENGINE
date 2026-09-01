#version 450
#extension GL_OVR_multiview2 : require

layout(num_views = 2) in;

layout(location = 0) in vec3 Position;

layout(location = 0) out vec3 NearWorldPos;
layout(location = 1) out vec3 FarWorldPos;
layout(location = 2) out vec2 FragClipXY;

uniform mat4 LeftEyeInverseViewMatrix;
uniform mat4 RightEyeInverseViewMatrix;
uniform mat4 LeftEyeInverseProjMatrix;
uniform mat4 RightEyeInverseProjMatrix;
uniform int DepthMode;
uniform int ClipDepthRange;

mat4 GetInverseViewMatrix()
{
    return gl_ViewID_OVR == 0 ? LeftEyeInverseViewMatrix : RightEyeInverseViewMatrix;
}

mat4 GetInverseProjMatrix()
{
    return gl_ViewID_OVR == 0 ? LeftEyeInverseProjMatrix : RightEyeInverseProjMatrix;
}

vec3 Unproject(vec2 clipXY, float clipZ)
{
    vec4 viewPos = GetInverseProjMatrix() * vec4(clipXY, clipZ, 1.0);
    float invW = abs(viewPos.w) > 1e-6 ? 1.0 / viewPos.w : 1.0;
    return (GetInverseViewMatrix() * (viewPos * invW)).xyz;
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

    NearWorldPos = Unproject(clipXY, GetNearClipZ());
    FarWorldPos = Unproject(clipXY, GetFarClipZ());

    gl_Position = vec4(clipXY, GetNearClipZ(), 1.0);
}
