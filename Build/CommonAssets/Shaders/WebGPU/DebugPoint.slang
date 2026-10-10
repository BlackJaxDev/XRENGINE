// Vertex expansion of the compressed desktop debug-point stream. The scaffold
// supplies four signed XY corners; instance_index selects one packed point.
struct DebugPointView
{
    float4x4 viewProjection;
    float4x4 inverseView;
};
struct DebugPointMaterial
{
    float4 parameters; // x = desktop PointSize, scaled by camera distance.
};
[[vk::binding(0, 0)]] ConstantBuffer<DebugPointView> view;
[[vk::binding(0, 1)]] ConstantBuffer<DebugPointMaterial> material;
[[vk::binding(0, 2)]] StructuredBuffer<uint> points;
struct DebugPointInput
{
    [[vk::location(0)]] float3 position : POSITION;
    uint instanceId : SV_InstanceID;
};
struct DebugPointOutput
{
    float4 position : SV_Position;
    nointerpolation float4 color : TEXCOORD0;
    float2 uv : TEXCOORD1;
};
float4 unpackColor(uint packed)
{
    return float4(float(packed & 255u), float((packed >> 8u) & 255u),
        float((packed >> 16u) & 255u), float((packed >> 24u) & 255u)) / 255.0;
}
[shader("vertex")]
DebugPointOutput debugPointVertex(DebugPointInput input)
{
    uint baseWord = input.instanceId * 4u;
    float3 center = float3(asfloat(points[baseWord]), asfloat(points[baseWord + 1u]),
        asfloat(points[baseWord + 2u]));
    float3 right = mul(view.inverseView, float4(1.0, 0.0, 0.0, 0.0)).xyz;
    float3 up = mul(view.inverseView, float4(0.0, 1.0, 0.0, 0.0)).xyz;
    float3 camera = mul(view.inverseView, float4(0.0, 0.0, 0.0, 1.0)).xyz;
    float distance = length(center - camera);
    float3 offset = (right * input.position.x + up * input.position.y) * (distance * material.parameters.x);
    DebugPointOutput output;
    output.position = mul(view.viewProjection, float4(center + offset, 1.0));
    output.color = unpackColor(points[baseWord + 3u]);
    output.uv = input.position.xy;
    return output;
}
[shader("fragment")]
float4 debugPointFragment(DebugPointOutput input) : SV_Target0
{
    float radius = length(input.uv);
    if (radius > 1.0)
        discard;
    float alpha = 1.0 - smoothstep(0.95, 1.0, radius);
    if (alpha <= 0.001)
        discard;
    return float4(input.color.xyz, input.color.w * alpha);
}
