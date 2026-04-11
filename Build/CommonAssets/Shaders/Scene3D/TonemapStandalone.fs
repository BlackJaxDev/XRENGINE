#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

#include "../Snippets/ToneMapping.glsl"

uniform sampler2D SourceTexture;
uniform sampler2D BloomTexture;
uniform bool UseBloom;
uniform float BloomStrength;
uniform float Exposure;
uniform float Gamma;
uniform int TonemapType = XRENGINE_TONEMAP_MOBIUS;
uniform float MobiusTransition = 0.6;

void main()
{
    vec2 uv = FragPos.xy;
    vec4 src = texture(SourceTexture, uv);
    vec3 color = src.rgb;

    if (UseBloom)
        color += texture(BloomTexture, uv).rgb * BloomStrength;

    OutColor = vec4(XRENGINE_ApplyToneMap(color, TonemapType, Exposure, Gamma, MobiusTransition), src.a);
}