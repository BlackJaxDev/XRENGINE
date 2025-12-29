#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 0) in vec3 FragClipPos;

uniform float SkyboxIntensity = 1.0;
uniform vec3 SkyboxTopColor = vec3(0.52, 0.74, 1.0);
uniform vec3 SkyboxBottomColor = vec3(0.05, 0.06, 0.08);

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
    float t = clamp(dir.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 col = mix(SkyboxBottomColor, SkyboxTopColor, t);
    OutColor = col * SkyboxIntensity;
}
