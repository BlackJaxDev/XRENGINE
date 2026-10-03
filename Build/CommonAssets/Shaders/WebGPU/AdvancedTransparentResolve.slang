// Exact mono browser companion of the shared Advanced output operation.
struct OutputUniforms
{
    float ScreenWidth;
    float ScreenHeight;
    float2 ScreenOrigin;
};
[[vk::binding(0, 0)]] ConstantBuffer<OutputUniforms> outputUniforms;
[[vk::binding(0, 1)]] Texture2D<float4> TransparentSceneCopyTex;
[[vk::binding(1, 1)]] SamplerState TransparentSceneCopyTexSampler;
[[vk::binding(2, 1)]] Texture2D<float4> TransparentAccumTex;
[[vk::binding(3, 1)]] SamplerState TransparentAccumTexSampler;
[[vk::binding(4, 1)]] Texture2D<float4> TransparentRevealageTex;
[[vk::binding(5, 1)]] SamplerState TransparentRevealageTexSampler;

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

[shader("fragment")]
float4 outputFragment(ScreenOutput input) : SV_Target0
{
    float2 uv = saturate((input.position.xy - ScreenOrigin) / max(float2(ScreenWidth, ScreenHeight), float2(1.0)));
    float4 sceneColor = TransparentSceneCopyTex.SampleLevel(TransparentSceneCopyTexSampler, uv, 0.0);
    float4 accum = TransparentAccumTex.SampleLevel(TransparentAccumTexSampler, uv, 0.0);
    float revealage = saturate(TransparentRevealageTex.SampleLevel(TransparentRevealageTexSampler, uv, 0.0).r);
    float opacity = saturate(1.0 - revealage);
    float3 transparentColor = accum.a > 1e-5 ? accum.rgb / accum.a : float3(0.0);
    return float4(lerp(sceneColor.rgb, transparentColor, opacity), max(sceneColor.a, opacity));
}
