#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 20) in vec3 FragPosLocal;

uniform sampler2D Texture0;

vec2 EncodeOcta(vec3 dir)
{
    dir = normalize(dir);
    // Swizzle: world Y (up) -> octahedral Z (center/corners discriminator)
    vec3 octDir = vec3(dir.x, dir.z, dir.y);
    octDir /= max(abs(octDir.x) + abs(octDir.y) + abs(octDir.z), 1e-5f);

    vec2 uv = octDir.xy;
    if (octDir.z < 0.0f)
    {
        vec2 signDir = vec2(octDir.x >= 0.0f ? 1.0f : -1.0f, octDir.y >= 0.0f ? 1.0f : -1.0f);
        uv = (1.0f - abs(uv.yx)) * signDir;
    }

    return uv * 0.5f + 0.5f;
}

void main()
{
    vec3 direction = normalize(FragPosLocal);
    vec2 uv = EncodeOcta(direction);
    OutColor = texture(Texture0, uv);
}
