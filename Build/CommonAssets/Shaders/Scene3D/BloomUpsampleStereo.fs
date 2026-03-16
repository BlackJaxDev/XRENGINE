#version 460
#extension GL_OVR_multiview2 : require

layout(location = 0) out vec3 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray SourceTexture;

uniform int SourceLOD;
uniform float Radius;

void main()
{
    vec2 uv = clamp(FragPos.xy, -1.0, 1.0) * 0.5 + 0.5;
    uv = clamp(uv, 0.0, 1.0);

    float lod = float(SourceLOD);
    float layer = float(gl_ViewID_OVR);
    vec2 texelSize = (1.0 / textureSize(SourceTexture, SourceLOD).xy) * Radius;

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

    OutColor = max(result, vec3(0.0));
}
