#version 450

#pragma snippet "ScreenSpaceUtils"

// 9-tap tent filter upsample for physically-based progressive bloom.
// Uses additive hardware blending (GL_ONE, GL_ONE) to accumulate the
// upsampled lower-resolution mip into the existing downsample content
// at the target mip.  After the full upsample chain, mip 1 contains
// the complete multi-scale bloom result.

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;

uniform int SourceLOD;    // Mip level to read from (lower-res, being upsampled)
uniform float Radius;     // Scale factor for the tent filter kernel
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
    vec2 texelSize = (1.0 / vec2(textureSize(SourceTexture, SourceLOD))) * Radius;

    // 9-tap tent filter (3x3 bilinear samples).
    // Weight pattern:
    //  1  2  1
    //  2  4  2  / 16
    //  1  2  1
    vec3 result = vec3(0.0);
    result += textureLod(SourceTexture, uv + texelSize * vec2(-1.0, -1.0), lod).rgb * 1.0;
    result += textureLod(SourceTexture, uv + texelSize * vec2( 0.0, -1.0), lod).rgb * 2.0;
    result += textureLod(SourceTexture, uv + texelSize * vec2( 1.0, -1.0), lod).rgb * 1.0;
    result += textureLod(SourceTexture, uv + texelSize * vec2(-1.0,  0.0), lod).rgb * 2.0;
    result += textureLod(SourceTexture, uv,                                lod).rgb * 4.0;
    result += textureLod(SourceTexture, uv + texelSize * vec2( 1.0,  0.0), lod).rgb * 2.0;
    result += textureLod(SourceTexture, uv + texelSize * vec2(-1.0,  1.0), lod).rgb * 1.0;
    result += textureLod(SourceTexture, uv + texelSize * vec2( 0.0,  1.0), lod).rgb * 2.0;
    result += textureLod(SourceTexture, uv + texelSize * vec2( 1.0,  1.0), lod).rgb * 1.0;
    result *= 1.0 / 16.0;

    // Scatter attenuates each upsample level so wider mip contributions fall off
    // naturally, preventing the accumulated bloom from acting as a flat exposure boost.
    OutColor = vec4(max(result, vec3(0.0)) * Scatter, 1.0);
}
