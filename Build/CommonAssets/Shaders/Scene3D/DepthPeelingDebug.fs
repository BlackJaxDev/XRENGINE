#version 460

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D DepthPeelColor0;
uniform sampler2D DepthPeelColor1;
uniform sampler2D DepthPeelColor2;
uniform sampler2D DepthPeelColor3;
uniform int PreviewLayer;

void main()
{
    vec2 uv = FragPos.xy * 0.5 + 0.5;
    vec4 color = PreviewLayer == 0 ? texture(DepthPeelColor0, uv)
        : PreviewLayer == 1 ? texture(DepthPeelColor1, uv)
        : PreviewLayer == 2 ? texture(DepthPeelColor2, uv)
        : texture(DepthPeelColor3, uv);
    OutColor = vec4(color.rgb, max(color.a, 1.0));
}
