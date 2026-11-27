#version 450

layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;

uniform sampler2D SourceTexture;
uniform vec2 SourceTexelSize; // XRENGINE_FREQUENCY(Pass)
uniform float BlurOffset;     // XRENGINE_FREQUENCY(Pass)
uniform int FlipSourceYOnVulkan; // XRENGINE_FREQUENCY(Pass)

vec2 resolveUv(vec2 clipXY)
{
    vec2 uv = clipXY * 0.5 + 0.5;
#ifdef XRENGINE_VULKAN
    if (FlipSourceYOnVulkan != 0)
        uv.y = 1.0 - uv.y;
#endif
    return uv;
}

void main()
{
    vec2 uv = resolveUv(FragPos.xy);
    vec2 halfPixel = SourceTexelSize * max(BlurOffset, 0.0);
    vec2 uvMin = SourceTexelSize * 0.5;
    vec2 uvMax = vec2(1.0) - uvMin;

    vec3 sum = texture(SourceTexture, clamp(uv, uvMin, uvMax)).rgb * 4.0;
    sum += texture(SourceTexture, clamp(uv + vec2(-halfPixel.x, -halfPixel.y), uvMin, uvMax)).rgb;
    sum += texture(SourceTexture, clamp(uv + vec2( halfPixel.x, -halfPixel.y), uvMin, uvMax)).rgb;
    sum += texture(SourceTexture, clamp(uv + vec2(-halfPixel.x,  halfPixel.y), uvMin, uvMax)).rgb;
    sum += texture(SourceTexture, clamp(uv + vec2( halfPixel.x,  halfPixel.y), uvMin, uvMax)).rgb;

    OutColor = vec4(sum * 0.125, 1.0);
}
