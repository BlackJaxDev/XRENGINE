#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 0) in vec3 FragClipPos;

uniform samplerCubeArray Texture0;
uniform float SkyboxIntensity = 1.0;
uniform float SkyboxRotation = 0.0;
uniform int CubemapLayer = 0;

// Camera matrices
uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;

vec3 GetWorldDirection(vec3 clipPos)
{
    mat4 invProj = inverse(ProjMatrix);
    vec4 viewPos = invProj * vec4(clipPos.xy, 1.0, 1.0);
    vec3 viewDir = normalize(viewPos.xyz / viewPos.w);
    mat3 camRotation = mat3(InverseViewMatrix);
    return normalize(camRotation * viewDir);
}

void main()
{
    vec3 dir = GetWorldDirection(FragClipPos);
    
    // Apply rotation around Y axis
    float cosRot = cos(SkyboxRotation);
    float sinRot = sin(SkyboxRotation);
    dir = vec3(
        dir.x * cosRot - dir.z * sinRot,
        dir.y,
        dir.x * sinRot + dir.z * cosRot
    );

    OutColor = texture(Texture0, vec4(dir, float(CubemapLayer))).rgb * SkyboxIntensity;
}
