#version 460
#extension GL_OVR_multiview2 : require

#pragma snippet "ScreenSpaceUtils"
#pragma snippet "ContrastAdaptiveSharpening"

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray PostProcessOutputTexture;
uniform float RcasSharpness = 0.0;

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
    float layer = float(gl_ViewID_OVR);
    vec4 centerSample = texture(PostProcessOutputTexture, vec3(sourceUv, layer));
    if (RcasSharpness <= 1e-5)
    {
        OutColor = centerSample;
        return;
    }

    vec2 texel = 1.0 / vec2(max(textureSize(PostProcessOutputTexture, 0).xy, ivec2(1)));
    vec2 minUv = texel * 0.5;
    vec2 maxUv = vec2(1.0) - minUv;
    vec3 b = texture(PostProcessOutputTexture, vec3(clamp(sourceUv + vec2(0.0, -texel.y), minUv, maxUv), layer)).rgb;
    vec3 d = texture(PostProcessOutputTexture, vec3(clamp(sourceUv + vec2(-texel.x, 0.0), minUv, maxUv), layer)).rgb;
    vec3 f = texture(PostProcessOutputTexture, vec3(clamp(sourceUv + vec2(texel.x, 0.0), minUv, maxUv), layer)).rgb;
    vec3 h = texture(PostProcessOutputTexture, vec3(clamp(sourceUv + vec2(0.0, texel.y), minUv, maxUv), layer)).rgb;
    OutColor = vec4(XRENGINE_EvaluateRcas(centerSample.rgb, b, d, f, h, RcasSharpness), centerSample.a);
}
