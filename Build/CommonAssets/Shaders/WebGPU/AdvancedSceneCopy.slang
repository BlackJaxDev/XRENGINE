// Exact mono browser companion of the shared Advanced output operation.
struct OutputUniforms
{
    float ScreenWidth;
    float ScreenHeight;
    float2 ScreenOrigin;
};
[[vk::binding(0, 0)]] ConstantBuffer<OutputUniforms> outputUniforms;
[[vk::binding(0, 1)]] Texture2D<float4> HDRSceneTex;
[[vk::binding(1, 1)]] SamplerState HDRSceneTexSampler;

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
    return HDRSceneTex.SampleLevel(HDRSceneTexSampler, uv, 0.0);
}
