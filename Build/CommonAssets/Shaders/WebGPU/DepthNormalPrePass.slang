// Forward opaque depth/normal replay. WebGPU clip depth is zero-to-one; the
// color target stores oct-encoded world normals in RG of an RGBA16F texture.
struct DepthNormalView { float4x4 viewProjection; };
struct DepthNormalObject { float4x4 modelMatrix; float4x4 normalMatrix; };
[[vk::binding(0, 0)]] ConstantBuffer<DepthNormalView> view;
[[vk::binding(1, 0)]] ConstantBuffer<DepthNormalObject> object;
#ifdef XRE_STANDARD_LIT_COVERAGE
struct CoverageNormalMaterial { float4 baseColorOpacity; float4 coverage; };
[[vk::binding(0, 1)]] ConstantBuffer<CoverageNormalMaterial> material;
#endif
struct DepthNormalInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
};
struct DepthNormalOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 worldNormal : TEXCOORD0;
};
[shader("vertex")]
DepthNormalOutput depthNormalVertex(DepthNormalInput input)
{
    DepthNormalOutput output;
    output.position = mul(view.viewProjection, mul(object.modelMatrix, float4(input.position, 1.0)));
    output.worldNormal = mul(object.normalMatrix, float4(input.normal, 0.0)).xyz;
    return output;
}
[shader("fragment")]
float4 depthNormalFragment(DepthNormalOutput input) : SV_Target0
{
#ifdef XRE_STANDARD_LIT_COVERAGE
    if (material.coverage.x > 0.5 && material.baseColorOpacity.w < material.coverage.y)
        discard;
#endif
    float3 normal = input.worldNormal * rsqrt(max(dot(input.worldNormal, input.worldNormal), 1e-20));
    float3 n = normal / max(abs(normal.x) + abs(normal.y) + abs(normal.z), 1e-6);
    float2 oct = n.xy;
    if (n.z < 0.0)
        oct = (1.0 - abs(oct.yx)) * float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return float4(oct * 0.5 + 0.5, 0.0, 1.0);
}
