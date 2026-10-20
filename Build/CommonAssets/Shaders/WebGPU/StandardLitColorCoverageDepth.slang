// The same uniform-alpha coverage controls color, normal and caster depth.
struct CoverageDepthView { float4x4 viewProjection; };
struct CoverageDepthObject { float4x4 modelMatrix; };
struct CoverageDepthMaterial { float4 baseColorOpacity; float4 coverage; };
[[vk::binding(0, 0)]] ConstantBuffer<CoverageDepthView> view;
[[vk::binding(1, 0)]] ConstantBuffer<CoverageDepthObject> object;
[[vk::binding(0, 1)]] ConstantBuffer<CoverageDepthMaterial> material;
struct CoverageDepthInput { [[vk::location(0)]] float3 position : POSITION; };
[shader("vertex")]
float4 coverageDepthVertex(CoverageDepthInput input) : SV_Position
{
    return mul(view.viewProjection, mul(object.modelMatrix, float4(input.position, 1.0)));
}
[shader("fragment")]
void coverageDepthFragment()
{
    if (material.coverage.x > 0.5 && material.baseColorOpacity.w < material.coverage.y)
        discard;
}
