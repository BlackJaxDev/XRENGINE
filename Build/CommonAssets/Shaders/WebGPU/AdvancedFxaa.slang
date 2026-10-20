// Exact mono browser companion of the shared Advanced output operation.
struct OutputUniforms
{
    float ScreenWidth;
    float ScreenHeight;
    float2 ScreenOrigin;
    float2 FxaaTexelStep;
};
[[vk::binding(0, 0)]] ConstantBuffer<OutputUniforms> outputUniforms;
[[vk::binding(0, 1)]] Texture2D<float4> Texture0;
[[vk::binding(1, 1)]] SamplerState Texture0Sampler;

struct ScreenInput { [[vk::location(0)]] float3 position : POSITION; };
struct ScreenOutput { float4 position : SV_Position; };
[shader("vertex")]
ScreenOutput outputVertex(ScreenInput input)
{
    ScreenOutput output; output.position = float4(input.position.xy, 0.0, 1.0); return output;
}
#define ScreenWidth outputUniforms.ScreenWidth
#define ScreenHeight outputUniforms.ScreenHeight
#define ScreenOrigin outputUniforms.ScreenOrigin
#define FxaaTexelStep outputUniforms.FxaaTexelStep

static const float3 LumaWeights = float3(0.299f, 0.587f, 0.114f);

float3 Sample(float2 uv)
{
    return Texture0.SampleLevel(Texture0Sampler, clamp(uv, float2(0.0f), float2(1.0f)), 0.0).rgb;
}

[shader("fragment")]
float4 outputFragment(ScreenOutput input) : SV_Target0
{
    float2 uv = clamp(
        (input.position.xy - ScreenOrigin) / max(float2(ScreenWidth, ScreenHeight), float2(1.0)),
        float2(0.0f),
        float2(1.0f));

    float3 rgbM = Sample(uv);
    float3 rgbNW = Sample(uv + float2(-FxaaTexelStep.x, -FxaaTexelStep.y));
    float3 rgbNE = Sample(uv + float2( FxaaTexelStep.x, -FxaaTexelStep.y));
    float3 rgbSW = Sample(uv + float2(-FxaaTexelStep.x,  FxaaTexelStep.y));
    float3 rgbSE = Sample(uv + float2( FxaaTexelStep.x,  FxaaTexelStep.y));

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
        return float4(rgbM, 1.0f);
    }

    float2 dir;
    dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
    dir.y =  ((lumaNW + lumaSW) - (lumaNE + lumaSE));

    float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25f * 0.0312f), 0.0078125f);
    float rcpDirMin = 1.0f / (min(abs(dir.x), abs(dir.y)) + dirReduce);
    dir = clamp(dir * rcpDirMin, float2(-8.0f), float2(8.0f)) * FxaaTexelStep;

    float3 rgbA = 0.5f * (Sample(uv + dir * (1.0f / 3.0f - 0.5f)) + Sample(uv + dir * (2.0f / 3.0f - 0.5f)));
    float3 rgbB = rgbA * 0.5f + 0.25f * (Sample(uv + dir * -0.5f) + Sample(uv + dir * 0.5f));
    float lumaB = dot(rgbB, LumaWeights);

    if ((lumaB < lumaMin) || (lumaB > lumaMax))
        return float4(rgbA, 1.0f);
    else
        return float4(rgbB, 1.0f);
}
