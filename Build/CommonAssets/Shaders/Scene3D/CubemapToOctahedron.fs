#version 450

#pragma snippet "OctahedralMapping"

layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;

uniform samplerCube Texture0;

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x < -1.0f || clipXY.x > 1.0f || clipXY.y < -1.0f || clipXY.y > 1.0f)
    {
        discard;
    }

    // Map from clip space [-1, 1] to UV space [0, 1]
    vec2 uv = clipXY * 0.5f + 0.5f;
    vec3 dir = XRENGINE_DecodeOcta(uv);
    OutColor = vec4(texture(Texture0, dir).rgb, 1.0f);
}
