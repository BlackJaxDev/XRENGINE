#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D PostProcessOutputTexture;
uniform vec2 FxaaTexelStep;

const vec3 LumaWeights = vec3(0.299f, 0.587f, 0.114f);

vec3 Sample(vec2 uv)
{
    return texture(PostProcessOutputTexture, clamp(uv, vec2(0.0f), vec2(1.0f))).rgb;
}

void main()
{
    vec2 uv = FragPos.xy;
    if (uv.x > 1.0f || uv.y > 1.0f)
        discard;

    uv = uv * 0.5f + 0.5f;

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

    if (lumaRange < max(0.0312f, lumaMax * 0.125f))
    {
        OutColor = vec4(rgbM, 1.0f);
        return;
    }

    vec2 dir;
    dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
    dir.y =  ((lumaNW + lumaSW) - (lumaNE + lumaSE));

    float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25f * 0.0312f), 0.0078125f);
    float rcpDirMin = 1.0f / (min(abs(dir.x), abs(dir.y)) + dirReduce);
    dir = clamp(dir * rcpDirMin, vec2(-8.0f), vec2(8.0f)) * FxaaTexelStep;

    vec3 rgbA = 0.5f * (Sample(uv + dir * (1.0f / 3.0f - 0.5f)) + Sample(uv + dir * (2.0f / 3.0f - 0.5f)));
    vec3 rgbB = rgbA * 0.5f + 0.25f * (Sample(uv + dir * -0.5f) + Sample(uv + dir * 0.5f));
    float lumaB = dot(rgbB, LumaWeights);

    if (lumaB < lumaMin || lumaB > lumaMax)
        OutColor = vec4(rgbA, 1.0f);
    else
        OutColor = vec4(rgbB, 1.0f);
}
