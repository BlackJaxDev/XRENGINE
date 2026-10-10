// Engine depth and shadow-caster vertex lowering. The host supplies the selected
// camera/light view-projection with zero-to-one depth and the engine model matrix.
struct ViewUniforms
{
    float4x4 viewProjection;
};

struct ObjectUniforms
{
    float4x4 modelMatrix;
};

[[vk::binding(0, 0)]] ConstantBuffer<ViewUniforms> view;
[[vk::binding(1, 0)]] ConstantBuffer<ObjectUniforms> object;

struct DepthVertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
};

[shader("vertex")]
float4 depthVertex(DepthVertexInput input) : SV_Position
{
    return mul(view.viewProjection, mul(object.modelMatrix, float4(input.position, 1.0)));
}

// Opt-in coordinate/depth diagnostic. The production depth recipe omits this
// fragment stage; the probe allows color readback of the actual rasterized depth.
[shader("fragment")]
float4 depthProbeFragment(float4 position : SV_Position) : SV_Target0
{
    return float4(position.z, 0.0, 1.0 - position.z, 1.0);
}
