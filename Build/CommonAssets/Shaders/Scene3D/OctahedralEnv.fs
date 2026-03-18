#version 450

#pragma snippet "OctahedralMapping"

layout(location = 0) out vec4 OutColor;
layout(location = 20) in vec3 FragPosLocal;

uniform sampler2D Texture0;

void main()
{
    vec3 direction = normalize(FragPosLocal);
    vec2 uv = XRENGINE_EncodeOcta(direction);
    OutColor = texture(Texture0, uv);
}
