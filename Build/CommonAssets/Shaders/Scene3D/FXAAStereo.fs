#version 460
#extension GL_OVR_multiview2 : require

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray Texture0;
uniform vec2 FxaaTexelStep;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform vec2 ScreenOrigin;

const vec3 LumaWeights = vec3(0.299, 0.587, 0.114);

vec3 Sample(vec2 uv)
{
    return texture(Texture0, vec3(clamp(uv, vec2(0.0), vec2(1.0)), float(gl_ViewID_OVR))).rgb;
}

void main()
{
    vec2 uv = XRENGINE_FramebufferUV(
        gl_FragCoord.xy,
        ScreenOrigin,
        vec2(ScreenWidth, ScreenHeight));

    vec3 rgbM = Sample(uv);
    vec3 rgbNW = Sample(uv + vec2(-FxaaTexelStep.x, -FxaaTexelStep.y));
    vec3 rgbNE = Sample(uv + vec2( FxaaTexelStep.x, -FxaaTexelStep.y));
    vec3 rgbSW = Sample(uv + vec2(-FxaaTexelStep.x,  FxaaTexelStep.y));
    vec3 rgbSE = Sample(uv + vec2( FxaaTexelStep.x,  FxaaTexelStep.y));

    float lumaM = dot(rgbM, LumaWeights);
    float lumaNW = dot(rgbNW, LumaWeights);
    float lumaNE = dot(rgbNE, LumaWeights);
    float lumaSW = dot(rgbSW, LumaWeights);
    float lumaSE = dot(rgbSE, LumaWeights);

    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));
    float lumaRange = lumaMax - lumaMin;

    if (lumaRange < max(0.0312, lumaMax * 0.125))
    {
        OutColor = vec4(rgbM, 1.0);
        return;
    }

    vec2 dir;
    dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
    dir.y =  ((lumaNW + lumaSW) - (lumaNE + lumaSE));

    float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25 * 0.0312), 0.0078125);
    float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
    dir = clamp(dir * rcpDirMin, vec2(-8.0), vec2(8.0)) * FxaaTexelStep;

    vec3 rgbA = 0.5 * (Sample(uv + dir * (1.0 / 3.0 - 0.5)) + Sample(uv + dir * (2.0 / 3.0 - 0.5)));
    vec3 rgbB = rgbA * 0.5 + 0.25 * (Sample(uv + dir * -0.5) + Sample(uv + dir * 0.5));
    float lumaB = dot(rgbB, LumaWeights);

    OutColor = lumaB < lumaMin || lumaB > lumaMax
        ? vec4(rgbA, 1.0)
        : vec4(rgbB, 1.0);
}
