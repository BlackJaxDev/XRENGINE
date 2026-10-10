struct AuthoredTexturedVertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
    [[vk::location(2)]] float2 uv : TEXCOORD0;
    [[vk::location(3)]] float4 tangent : TANGENT;
    [[vk::location(4)]] float tangentPresent : TANGENTPRESENCE;
};
struct AuthoredTexturedVertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 worldPosition : TEXCOORD0;
    [[vk::location(1)]] float3 worldNormal : TEXCOORD1;
    [[vk::location(2)]] float2 uv : TEXCOORD2;
    [[vk::location(3)]] float3 worldTangent : TEXCOORD3;
    [[vk::location(4)]] float3 worldBitangent : TEXCOORD4;
};
AuthoredTexturedVertexOutput authoredTexturedVertex(AuthoredTexturedVertexInput input)
{
    AuthoredTexturedVertexOutput output;
    float4 world = mul(object.modelMatrix, float4(input.position, 1.0));
    output.position = mul(view.viewProjection, world);
    output.worldPosition = world.xyz;
    output.worldNormal = normalize(mul(object.normalMatrix, float4(input.normal, 0.0)).xyz);
    // Preserve independent bases and zero sentinels; the fragment owns validation.
    float3 localBitangent = cross(input.normal, input.tangent.xyz) * input.tangent.w;
    output.worldTangent = input.tangentPresent != 0.0
        ? normalize(mul(object.normalMatrix, float4(input.tangent.xyz, 0.0)).xyz) : float3(0.0);
    output.worldBitangent = input.tangentPresent != 0.0
        ? normalize(mul(object.normalMatrix, float4(localBitangent, 0.0)).xyz) : float3(0.0);
    output.uv = input.uv;
    return output;
}
