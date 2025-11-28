#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 20) in vec3 FragPosLocal;

uniform sampler2D Texture0;

vec2 EncodeOcta(vec3 dir)
{
    dir = normalize(dir);
    dir /= max(abs(dir.x) + abs(dir.y) + abs(dir.z), 1e-5f);

    vec2 uv = dir.xy;
    if (dir.z < 0.0f)
    {
        vec2 signDir = vec2(dir.x >= 0.0f ? 1.0f : -1.0f, dir.y >= 0.0f ? 1.0f : -1.0f);
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
