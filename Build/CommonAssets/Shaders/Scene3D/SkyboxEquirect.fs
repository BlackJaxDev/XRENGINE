#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 0) in vec3 FragClipPos;

uniform sampler2D Texture0;
uniform float SkyboxIntensity = 1.0;
uniform float SkyboxRotation = 0.0;

// Camera matrices - InverseViewMatrix is the camera's world transform
uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;

const float PI = 3.14159265359;

vec3 GetWorldDirection(vec3 clipPos)
{
    // Reconstruct view-space ray direction from clip coordinates
    // Use inverse projection to go from clip space to view space
    mat4 invProj = inverse(ProjMatrix);
    vec4 viewPos = invProj * vec4(clipPos.xy, 1.0, 1.0);
    vec3 viewDir = normalize(viewPos.xyz / viewPos.w);
    
    // Transform view direction to world space using camera's world transform
    // InverseViewMatrix is the camera's world matrix (position + orientation)
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

    // Convert direction to spherical coordinates
    float phi = atan(dir.z, dir.x);
    float theta = asin(clamp(dir.y, -1.0, 1.0));
    
    // Map to UV coordinates
    vec2 uv = vec2((phi / (2.0 * PI)) + 0.5, 1.0 - ((theta / PI) + 0.5));

    OutColor = texture(Texture0, uv).rgb * SkyboxIntensity;
}
