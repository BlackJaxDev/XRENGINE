#version 460

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D TransparentSceneCopyTex;
uniform sampler2D DepthPeelColor0;
uniform sampler2D DepthPeelColor1;
uniform sampler2D DepthPeelColor2;
uniform sampler2D DepthPeelColor3;
uniform int ActiveDepthPeelLayers;

vec4 SampleLayer(int layerIndex, vec2 uv)
{
    switch (layerIndex)
    {
        case 0: return texture(DepthPeelColor0, uv);
        case 1: return texture(DepthPeelColor1, uv);
        case 2: return texture(DepthPeelColor2, uv);
        case 3: return texture(DepthPeelColor3, uv);
        default: return vec4(0.0);
    }
}

void main()
{
    vec2 uv = FragPos.xy * 0.5 + 0.5;
    vec4 composite = texture(TransparentSceneCopyTex, uv);
    for (int layerIndex = ActiveDepthPeelLayers - 1; layerIndex >= 0; --layerIndex)
    {
        vec4 src = SampleLayer(layerIndex, uv);
        composite.rgb = src.rgb * src.a + composite.rgb * (1.0 - src.a);
        composite.a = src.a + composite.a * (1.0 - src.a);
    }
    OutColor = composite;
}
