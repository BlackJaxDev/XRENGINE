// Indexed three-vertex scaffold expands one packed desktop debug triangle.
struct DebugTriangleView
{
    float4x4 viewProjection;
};
[[vk::binding(0, 0)]] ConstantBuffer<DebugTriangleView> view;
[[vk::binding(0, 2)]] StructuredBuffer<uint> triangles;
struct DebugTriangleInput
{
    [[vk::location(0)]] float3 position : POSITION;
    uint instanceId : SV_InstanceID;
};
struct DebugTriangleOutput
{
    float4 position : SV_Position;
    nointerpolation float4 color : TEXCOORD0;
};
float4 unpackColor(uint packed)
{
    return float4(float(packed & 255u), float((packed >> 8u) & 255u),
        float((packed >> 16u) & 255u), float((packed >> 24u) & 255u)) / 255.0;
}
[shader("vertex")]
DebugTriangleOutput debugTriangleVertex(DebugTriangleInput input)
{
    uint baseWord = input.instanceId * 10u;
    uint corner = min(uint(input.position.x), 2u);
    uint cornerWord = baseWord + corner * 3u;
    float3 point = float3(asfloat(triangles[cornerWord]), asfloat(triangles[cornerWord + 1u]),
        asfloat(triangles[cornerWord + 2u]));
    DebugTriangleOutput output;
    output.position = mul(view.viewProjection, float4(point, 1.0));
    output.color = unpackColor(triangles[baseWord + 9u]);
    return output;
}
[shader("fragment")]
float4 debugTriangleFragment(DebugTriangleOutput input) : SV_Target0
{
    return input.color;
}
