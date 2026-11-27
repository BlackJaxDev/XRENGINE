#version 450

#pragma snippet "ScreenSpaceUtils"
#pragma snippet "ContrastAdaptiveSharpening"

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D Texture0;
uniform float RcasSharpness = 0.0f; // XRENGINE_FREQUENCY(Pass)
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform vec2 ScreenOrigin;

void main()
{
    vec2 uv = clamp(
        XRENGINE_FramebufferUV(
            gl_FragCoord.xy,
            ScreenOrigin,
            vec2(ScreenWidth, ScreenHeight)),
        vec2(0.0f),
        vec2(1.0f));

    ivec2 extent = max(textureSize(Texture0, 0), ivec2(1));
    ivec2 maxCoord = extent - ivec2(1);
    ivec2 center = clamp(ivec2(floor(uv * vec2(extent))), ivec2(0), maxCoord);

    vec4 centerSample = texelFetch(Texture0, center, 0);
    if (RcasSharpness <= 1e-5f)
    {
        OutColor = centerSample;
        return;
    }

    vec3 b = texelFetch(Texture0, clamp(center + ivec2(0, -1), ivec2(0), maxCoord), 0).rgb;
    vec3 d = texelFetch(Texture0, clamp(center + ivec2(-1, 0), ivec2(0), maxCoord), 0).rgb;
    vec3 f = texelFetch(Texture0, clamp(center + ivec2(1, 0), ivec2(0), maxCoord), 0).rgb;
    vec3 h = texelFetch(Texture0, clamp(center + ivec2(0, 1), ivec2(0), maxCoord), 0).rgb;

    vec3 sharpened = XRENGINE_EvaluateRcas(centerSample.rgb, b, d, f, h, RcasSharpness);
    OutColor = vec4(sharpened, centerSample.a);
}
