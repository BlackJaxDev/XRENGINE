// Exact mono browser companion of the shared Advanced output operation.
struct OutputUniforms
{
    float ScreenWidth;
    float ScreenHeight;
    float2 ScreenOrigin;
};
[[vk::binding(0, 0)]] ConstantBuffer<OutputUniforms> outputUniforms;
[[vk::binding(0, 1)]] Texture2D<float4> SourceTexture;
[[vk::binding(1, 1)]] SamplerState SourceTextureSampler;

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
    float4 color = SourceTexture.SampleLevel(SourceTextureSampler, uv, 0.0);
    // The canvas uses a non-sRGB attachment. This is the same final transfer
    // desktop receives from its sRGB attachment, after all linear post passes.
    float3 linear = max(color.rgb, float3(0.0));
    float3 higher = pow(linear, float3(1.0 / 2.4)) * 1.055 - 0.055;
    return float4(lerp(linear * 12.92, higher, step(float3(0.0031308), linear)), color.a);
}
