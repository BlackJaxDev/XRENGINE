#version 460
#extension GL_OVR_multiview2 : require

#pragma snippet "ScreenSpaceUtils"
#pragma snippet "ContrastAdaptiveSharpening"

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray Texture0;
uniform float RcasSharpness = 0.0; // XRENGINE_FREQUENCY(Pass)
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
        vec2(0.0),
        vec2(1.0));

    ivec2 extent = max(textureSize(Texture0, 0).xy, ivec2(1));
    ivec2 maxCoord = extent - ivec2(1);
    ivec2 center = clamp(ivec2(floor(uv * vec2(extent))), ivec2(0), maxCoord);
    int layer = int(gl_ViewID_OVR);

    vec4 centerSample = texelFetch(Texture0, ivec3(center, layer), 0);
    if (RcasSharpness <= 1e-5)
    {
        OutColor = centerSample;
        return;
    }

    vec3 b = texelFetch(Texture0, ivec3(clamp(center + ivec2(0, -1), ivec2(0), maxCoord), layer), 0).rgb;
    vec3 d = texelFetch(Texture0, ivec3(clamp(center + ivec2(-1, 0), ivec2(0), maxCoord), layer), 0).rgb;
    vec3 f = texelFetch(Texture0, ivec3(clamp(center + ivec2(1, 0), ivec2(0), maxCoord), layer), 0).rgb;
    vec3 h = texelFetch(Texture0, ivec3(clamp(center + ivec2(0, 1), ivec2(0), maxCoord), layer), 0).rgb;

    vec3 sharpened = XRENGINE_EvaluateRcas(centerSample.rgb, b, d, f, h, RcasSharpness);
    OutColor = vec4(sharpened, centerSample.a);
}
