struct ViewUniforms
{
    float4x4 transform;
};

struct MaterialUniforms
{
    float4 tint;
};

[[vk::binding(0, 0)]] ConstantBuffer<ViewUniforms> view;
[[vk::binding(0, 1)]] ConstantBuffer<MaterialUniforms> material;
[[vk::binding(1, 1)]] Texture2D<float4> colorTexture;
[[vk::binding(2, 1)]] SamplerState colorSampler;

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float2 uv : TEXCOORD0;
};

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    VertexOutput output;
    output.position = mul(view.transform, float4(input.position, 1.0));
    output.uv = input.uv;
    return output;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    float4 sampledColor = colorTexture.Sample(colorSampler, input.uv);
    return float4(sampledColor.rgb * material.tint.rgb, 1.0);
}
