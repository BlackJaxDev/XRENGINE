#version 450 core

layout(location = 0) out vec4 OutColor;

uniform usampler2D TransformId;
uniform float ScreenWidth;
uniform float ScreenHeight;

vec3 HashColor(uint id)
{
    // Cheap integer hash -> RGB in [0,1]
    uint x = id;
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    x ^= x >> 16;

    return vec3(
        float((x >> 0) & 255u),
        float((x >> 8) & 255u),
        float((x >> 16) & 255u)) / 255.0;
}

void main()
{
    if (ScreenWidth <= 0.0 || ScreenHeight <= 0.0)
    {
        OutColor = vec4(0.0);
        return;
    }

    vec2 uv = gl_FragCoord.xy / vec2(ScreenWidth, ScreenHeight);
    uint id = texture(TransformId, uv).r;

    if (id == 0u)
    {
        OutColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    OutColor = vec4(HashColor(id), 1.0);
}
