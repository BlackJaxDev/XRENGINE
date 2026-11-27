#version 450

#pragma snippet "ScreenSpaceUtils"
#pragma snippet "ContrastAdaptiveSharpening"

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D PostProcessOutputTexture;
uniform float RcasSharpness = 0.0f;

uniform float ScreenWidth;
uniform float ScreenHeight;
uniform vec2 ScreenOrigin;

#include "../Snippets/LensDistortion.glsl"

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x > 1.0 || clipXY.y > 1.0)
        discard;

    vec2 uv = clamp(
        XRENGINE_FramebufferUV(gl_FragCoord.xy, ScreenOrigin, vec2(ScreenWidth, ScreenHeight)),
        vec2(0.0),
        vec2(1.0));
    vec2 sourceUv = clamp(ApplyLensDistortionByMode(uv), vec2(0.0), vec2(1.0));
    vec4 centerSample = texture(PostProcessOutputTexture, sourceUv);
    if (RcasSharpness <= 1e-5f)
    {
        OutColor = centerSample;
        return;
    }

    vec2 texel = 1.0f / vec2(max(textureSize(PostProcessOutputTexture, 0), ivec2(1)));
    vec2 minUv = texel * 0.5f;
    vec2 maxUv = vec2(1.0f) - minUv;
    vec3 b = texture(PostProcessOutputTexture, clamp(sourceUv + vec2(0.0f, -texel.y), minUv, maxUv)).rgb;
    vec3 d = texture(PostProcessOutputTexture, clamp(sourceUv + vec2(-texel.x, 0.0f), minUv, maxUv)).rgb;
    vec3 f = texture(PostProcessOutputTexture, clamp(sourceUv + vec2(texel.x, 0.0f), minUv, maxUv)).rgb;
    vec3 h = texture(PostProcessOutputTexture, clamp(sourceUv + vec2(0.0f, texel.y), minUv, maxUv)).rgb;
    OutColor = vec4(XRENGINE_EvaluateRcas(centerSample.rgb, b, d, f, h, RcasSharpness), centerSample.a);
}
