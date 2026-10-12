struct AuthoredDepthView { float4x4 viewProjection; };
struct AuthoredDepthObject { float4x4 modelMatrix; float4x4 normalMatrix; };
[[vk::binding(0, 0)]] ConstantBuffer<AuthoredDepthView> view;
[[vk::binding(1, 0)]] ConstantBuffer<AuthoredDepthObject> object;
#include "AuthoredTexturedSampling.slang"
#include "AuthoredTexturedVertex.slang"
[shader("vertex")]
AuthoredTexturedVertexOutput depthNormalVertex(AuthoredTexturedVertexInput input)
{
    return authoredTexturedVertex(input);
}
[shader("fragment")]
float4 depthNormalFragment(AuthoredTexturedVertexOutput input) : SV_Target0
{
    float4 color = authoredTexturedColor(input.uv);
    float3 normal = authoredTexturedSurfaceNormal(input.uv, input.worldPosition,
        input.worldNormal, input.worldTangent, input.worldBitangent);
    authoredTexturedCoverage(color.a);
    float3 n = normal / max(abs(normal.x) + abs(normal.y) + abs(normal.z), 1e-6);
    float2 oct = n.xy;
    if (n.z < 0.0)
        oct = (1.0 - abs(oct.yx)) * float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return float4(oct * 0.5 + 0.5, 0.0, 1.0);
}
