#version 460
#extension GL_OVR_multiview2 : require

layout(location = 0) in vec3 FragPos;
layout(location = 0) out vec4 OutColor;

uniform sampler2DArray SourceTexture;
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
    float layer = float(gl_ViewID_OVR);

    vec3 sum = texture(SourceTexture, vec3(clamp(uv + vec2(-halfPixel.x * 2.0, 0.0), uvMin, uvMax), layer)).rgb;
    sum += texture(SourceTexture, vec3(clamp(uv + vec2(-halfPixel.x,  halfPixel.y), uvMin, uvMax), layer)).rgb * 2.0;
    sum += texture(SourceTexture, vec3(clamp(uv + vec2( 0.0,          halfPixel.y * 2.0), uvMin, uvMax), layer)).rgb;
    sum += texture(SourceTexture, vec3(clamp(uv + vec2( halfPixel.x,  halfPixel.y), uvMin, uvMax), layer)).rgb * 2.0;
    sum += texture(SourceTexture, vec3(clamp(uv + vec2( halfPixel.x * 2.0, 0.0), uvMin, uvMax), layer)).rgb;
    sum += texture(SourceTexture, vec3(clamp(uv + vec2( halfPixel.x, -halfPixel.y), uvMin, uvMax), layer)).rgb * 2.0;
    sum += texture(SourceTexture, vec3(clamp(uv + vec2( 0.0,         -halfPixel.y * 2.0), uvMin, uvMax), layer)).rgb;
    sum += texture(SourceTexture, vec3(clamp(uv + vec2(-halfPixel.x, -halfPixel.y), uvMin, uvMax), layer)).rgb * 2.0;

    OutColor = vec4(sum / 12.0, 1.0);
}
