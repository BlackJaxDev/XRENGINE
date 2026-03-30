#version 450

layout(location = 0) in vec3 Position;
layout(location = 0) out vec3 FragClipPos;
layout(location = 1) out vec3 FragWorldDir;

uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform float SkyboxRotation = 0.0;

vec3 GetWorldRay(vec2 clipXY)
{
    vec4 viewPos = InverseProjMatrix * vec4(clipXY, 1.0, 1.0);
    float invW = abs(viewPos.w) > 1e-6 ? 1.0 / viewPos.w : 1.0;
    vec3 viewRay = viewPos.xyz * invW;
    return mat3(InverseViewMatrix) * viewRay;
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

void main()
{
    vec2 clipXY = Position.xy;
    FragClipPos = Position;
    FragWorldDir = RotateSkyDirection(GetWorldRay(clipXY));
    // Output at maximum depth (z=1) so skybox is behind everything
    gl_Position = vec4(clipXY, 1.0, 1.0);
}
