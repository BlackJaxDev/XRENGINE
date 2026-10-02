// Preserve the shared spot caster's projected depth in its R16Float color map.
// Hardware raster depth independently keeps the nearest surface.
struct SpotDepthView { float4x4 viewProjection; };
struct SpotDepthObject { float4x4 modelMatrix; };
#ifdef XRE_STANDARD_LIT_COVERAGE
struct SpotDepthMaterial { float4 baseColorOpacity; float4 coverage; };
[[vk::binding(0, 1)]] ConstantBuffer<SpotDepthMaterial> material;
#endif
[[vk::binding(0, 0)]] ConstantBuffer<SpotDepthView> view;
[[vk::binding(1, 0)]] ConstantBuffer<SpotDepthObject> object;
struct SpotDepthInput { [[vk::location(0)]] float3 position : POSITION; };
[shader("vertex")]
float4 spotShadowDepthVertex(SpotDepthInput input) : SV_Position
{
    return mul(view.viewProjection, mul(object.modelMatrix, float4(input.position, 1.0)));
}
[shader("fragment")]
float spotShadowDepthFragment(float4 position : SV_Position) : SV_Target0
{
#ifdef XRE_STANDARD_LIT_COVERAGE
    if (material.coverage.x > 0.5 && material.baseColorOpacity.w < material.coverage.y)
        discard;
#endif
    return position.z;
}
