#version 450

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D PostProcessOutputTexture;

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
    OutColor = texture(PostProcessOutputTexture, sourceUv);
}
