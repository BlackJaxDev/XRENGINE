#version 450
#extension GL_OVR_multiview2 : require

layout(num_views = 2) in;

layout(location = 0) in vec3 Position;
layout(location = 0) out vec3 FragClipPos;
layout(location = 1) out vec3 FragWorldDir;

uniform mat4 LeftEyeInverseViewMatrix;
uniform mat4 RightEyeInverseViewMatrix;
uniform mat4 LeftEyeInverseProjMatrix;
uniform mat4 RightEyeInverseProjMatrix;
uniform int DepthMode;
uniform int ClipDepthRange;
uniform float SkyboxRotation = 0.0;

mat4 GetInverseViewMatrix()
{
    return gl_ViewID_OVR == 0 ? LeftEyeInverseViewMatrix : RightEyeInverseViewMatrix;
}

mat4 GetInverseProjMatrix()
{
    return gl_ViewID_OVR == 0 ? LeftEyeInverseProjMatrix : RightEyeInverseProjMatrix;
}

vec3 GetWorldRay(vec2 clipXY)
{
    vec4 viewPos = GetInverseProjMatrix() * vec4(clipXY, 1.0, 1.0);
    float invW = abs(viewPos.w) > 1e-6 ? 1.0 / viewPos.w : 1.0;
    vec3 viewRay = viewPos.xyz * invW;
    return mat3(GetInverseViewMatrix()) * viewRay;
}

vec3 RotateSkyDirection(vec3 dir)
{
    float cosRot = cos(SkyboxRotation);
    float sinRot = sin(SkyboxRotation);
    return vec3(
        dir.x * cosRot - dir.z * sinRot,
        dir.y,
        dir.x * sinRot + dir.z * cosRot);
}

float GetFarClipZ()
{
    if (DepthMode == 1)
        return ClipDepthRange == 1 ? -1.0 : 0.0;

    return 1.0;
}

void main()
{
    vec2 clipXY = Position.xy;
    FragClipPos = Position;
    FragWorldDir = RotateSkyDirection(GetWorldRay(clipXY));
    gl_Position = vec4(clipXY, GetFarClipZ(), 1.0);
}
