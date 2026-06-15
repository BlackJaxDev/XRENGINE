#version 460
#extension GL_OVR_multiview2 : require

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray SourceTexture;

uniform int SourceLOD;
uniform float Radius;
uniform float Scatter = 0.919; // Energy attenuation per upsample level (0=tight, 1=wide)
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform vec2 ScreenOrigin;

void main()
{
    vec2 uv = clamp(
        XRENGINE_FramebufferUV(gl_FragCoord.xy, ScreenOrigin, vec2(ScreenWidth, ScreenHeight)),
        vec2(0.0),
        vec2(1.0));

    float lod = float(SourceLOD);
    float layer = float(gl_ViewID_OVR);
    vec2 texelSize = (1.0 / vec2(textureSize(SourceTexture, SourceLOD).xy)) * Radius;

    vec3 result = vec3(0.0);
    result += textureLod(SourceTexture, vec3(uv + texelSize * vec2(-1.0, -1.0), layer), lod).rgb * 1.0;
    result += textureLod(SourceTexture, vec3(uv + texelSize * vec2( 0.0, -1.0), layer), lod).rgb * 2.0;
    result += textureLod(SourceTexture, vec3(uv + texelSize * vec2( 1.0, -1.0), layer), lod).rgb * 1.0;
    result += textureLod(SourceTexture, vec3(uv + texelSize * vec2(-1.0,  0.0), layer), lod).rgb * 2.0;
    result += textureLod(SourceTexture, vec3(uv,                                layer), lod).rgb * 4.0;
    result += textureLod(SourceTexture, vec3(uv + texelSize * vec2( 1.0,  0.0), layer), lod).rgb * 2.0;
    result += textureLod(SourceTexture, vec3(uv + texelSize * vec2(-1.0,  1.0), layer), lod).rgb * 1.0;
    result += textureLod(SourceTexture, vec3(uv + texelSize * vec2( 0.0,  1.0), layer), lod).rgb * 2.0;
    result += textureLod(SourceTexture, vec3(uv + texelSize * vec2( 1.0,  1.0), layer), lod).rgb * 1.0;
    result *= 1.0 / 16.0;

    OutColor = vec4(max(result, vec3(0.0)) * Scatter, 1.0);
}
