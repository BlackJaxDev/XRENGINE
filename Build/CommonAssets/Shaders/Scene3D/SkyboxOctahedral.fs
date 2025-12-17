#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 0) in vec3 FragClipPos;

uniform sampler2D Texture0;
uniform float SkyboxIntensity = 1.0;
uniform float SkyboxRotation = 0.0;

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

vec2 EncodeOcta(vec3 dir)
{
    dir = normalize(dir);
    // Swizzle: world Y (up) -> octahedral Z
    vec3 octDir = vec3(dir.x, dir.z, dir.y);
    octDir /= max(abs(octDir.x) + abs(octDir.y) + abs(octDir.z), 1e-5);

    vec2 uv = octDir.xy;
    if (octDir.z < 0.0)
    {
        vec2 signDir = vec2(octDir.x >= 0.0 ? 1.0 : -1.0, octDir.y >= 0.0 ? 1.0 : -1.0);
        uv = (1.0 - abs(uv.yx)) * signDir;
    }

    return uv * 0.5 + 0.5;
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

    vec2 uv = EncodeOcta(dir);
    OutColor = texture(Texture0, uv).rgb * SkyboxIntensity;
}
