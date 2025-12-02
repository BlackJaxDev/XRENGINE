#version 450

layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;

uniform samplerCube Texture0;

vec3 DecodeOcta(vec2 uv)
{
    vec2 f = uv * 2.0f - 1.0f;
    
    // Standard octahedral decode: center of texture = +Z in octahedral space
    vec3 n = vec3(f.x, f.y, 1.0f - abs(f.x) - abs(f.y));

    if (n.z < 0.0f)
    {
        // Fold corners for lower hemisphere
        vec2 signDir = vec2(n.x >= 0.0f ? 1.0f : -1.0f, n.y >= 0.0f ? 1.0f : -1.0f);
        n.xy = (1.0f - abs(n.yx)) * signDir;
    }

    // Map from octahedral space to world space:
    // Octahedral (X, Y, Z) -> World (X, Z, Y)
    // This puts octahedral +Z at world +Y (top)
    vec3 dir = vec3(n.x, n.z, n.y);
    return normalize(dir);
}

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x < -1.0f || clipXY.x > 1.0f || clipXY.y < -1.0f || clipXY.y > 1.0f)
    {
        discard;
    }

    // Map from clip space [-1, 1] to UV space [0, 1]
    vec2 uv = clipXY * 0.5f + 0.5f;
    vec3 dir = DecodeOcta(uv);
    OutColor = texture(Texture0, dir);
}
