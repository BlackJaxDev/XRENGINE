// C# Matrix4x4 rows are stored as four float4 values per UI quad.
#include "UIOutput.slang"
[[vk::binding(0, 1)]] StructuredBuffer<float4> quadTransforms;
[[vk::binding(1, 1)]] StructuredBuffer<float4> quadColors;
[[vk::binding(2, 1)]] StructuredBuffer<float4> quadBounds;

struct UIQuadInput
{
    [[vk::location(0)]] float3 position : POSITION;
    uint instanceId : SV_InstanceID;
};
struct UIQuadOutput
{
    float4 position : SV_Position;
    nointerpolation float4 color : TEXCOORD0;
    nointerpolation float4 bounds : TEXCOORD1;
};
[shader("vertex")]
UIQuadOutput uiQuadVertex(UIQuadInput input)
{
    uint baseRow = input.instanceId * 4u;
    float4 local = float4(input.position, 1.0);
    float4 world = local.x * quadTransforms[baseRow] +
        local.y * quadTransforms[baseRow + 1u] +
        local.z * quadTransforms[baseRow + 2u] +
        local.w * quadTransforms[baseRow + 3u];
    UIQuadOutput output;
    output.position = mul(view.viewProjection, world);
    output.color = quadColors[input.instanceId];
    output.bounds = quadBounds[input.instanceId];
    return output;
}
[shader("fragment")]
float4 uiQuadFragment(UIQuadOutput input) : SV_Target0
{
    if (any(isnan(input.bounds)))
        discard;
    return uiSolidOutput(input.color);
}
