// Position-only fullscreen quad. Its gradient makes shader execution visible
// independently of the output clear color and camera scene contents.
struct ModularGradientInput
{
    [[vk::location(0)]] float3 position : POSITION;
};

struct ModularGradientOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};

[shader("vertex")]
ModularGradientOutput modularGradientVertex(ModularGradientInput input)
{
    ModularGradientOutput output;
    output.position = float4(input.position.xy, 0.0, 1.0);
    output.uv = input.position.xy * float2(0.5, -0.5) + 0.5;
    return output;
}

[shader("fragment")]
float4 modularGradientFragment(ModularGradientOutput input) : SV_Target0
{
    return float4(input.uv.x, 0.18, 1.0 - input.uv.x, 1.0);
}
