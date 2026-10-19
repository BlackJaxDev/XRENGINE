#version 450
#pragma snippet "OctahedralMapping"
layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;
uniform samplerCube Texture0;
uniform float SourceLevel;

void main()
{
    vec3 direction = XRENGINE_DecodeOcta(FragPos.xy * 0.5 + 0.5);
    OutColor = vec4(textureLod(Texture0, direction, SourceLevel).rgb, 1.0);
}
