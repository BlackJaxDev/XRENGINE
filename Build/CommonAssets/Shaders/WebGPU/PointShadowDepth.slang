// The existing six light cameras select the nearest caster by face projection
// depth. The R16Float color cube independently stores normalized radial distance.
struct PointDepthView { float4x4 viewProjection; };
struct PointDepthObject { float4x4 modelMatrix; };
struct PointDepthLight { float3 lightPosition; float farPlaneDistance; };
#ifdef XRE_STANDARD_LIT_COVERAGE
struct PointDepthMaterial { float4 baseColorOpacity; float4 coverage; };
[[vk::binding(0, 1)]] ConstantBuffer<PointDepthMaterial> material;
#endif
[[vk::binding(0, 0)]] ConstantBuffer<PointDepthView> view;
[[vk::binding(1, 0)]] ConstantBuffer<PointDepthObject> object;
[[vk::binding(2, 0)]] ConstantBuffer<PointDepthLight> light;
struct PointDepthInput { [[vk::location(0)]] float3 position : POSITION; };
struct PointDepthOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 worldPosition : TEXCOORD0;
};
[shader("vertex")]
PointDepthOutput pointShadowDepthVertex(PointDepthInput input)
{
    PointDepthOutput output;
    float4 world = mul(object.modelMatrix, float4(input.position, 1.0));
    output.position = mul(view.viewProjection, world);
    // Shared cube cameras use OpenGL face up vectors. A WebGPU framebuffer has
    // top-left origin; invert the projected Y to retain the cube sampling axes.
    output.position.y = -output.position.y;
    output.worldPosition = world.xyz;
    return output;
}
[shader("fragment")]
float pointShadowDepthFragment(PointDepthOutput input) : SV_Target0
{
#ifdef XRE_STANDARD_LIT_COVERAGE
    if (material.coverage.x > 0.5 && material.baseColorOpacity.w < material.coverage.y)
        discard;
#endif
    return saturate(length(input.worldPosition - light.lightPosition) / max(light.farPlaneDistance, 0.0001));
}
