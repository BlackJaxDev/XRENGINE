#version 450

layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;

uniform samplerCube Texture0;

vec3 DecodeOcta(vec2 uv)
{
    vec2 f = uv * 2.0f - 1.0f;
    vec3 n = vec3(f.x, f.y, 1.0f - abs(f.x) - abs(f.y));

    if (n.z < 0.0f)
    {
        vec2 sign = vec2(f.x >= 0.0f ? 1.0f : -1.0f, f.y >= 0.0f ? 1.0f : -1.0f);
        n.x = sign.x * (1.0f - abs(n.y));
        n.y = sign.y * (1.0f - abs(n.x));
    }

    return normalize(n);
}

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x < -1.0f || clipXY.x > 1.0f || clipXY.y < -1.0f || clipXY.y > 1.0f)
    {
        discard;
    }

    vec2 uv = clipXY * 0.5f + 0.5f;
    vec3 dir = DecodeOcta(uv);
    OutColor = texture(Texture0, dir);
}
