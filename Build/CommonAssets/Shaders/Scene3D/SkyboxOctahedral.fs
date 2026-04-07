#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 1) in vec3 FragWorldDir;

uniform sampler2D Texture0;
uniform float SkyboxIntensity = 1.0;

vec2 EncodeOcta(vec3 dir)
{
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
    vec3 dir = normalize(FragWorldDir);

    vec2 uv = EncodeOcta(dir);
    OutColor = texture(Texture0, uv).rgb * SkyboxIntensity;
}
