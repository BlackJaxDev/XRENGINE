#include "UberBaseSurface.slang"

#ifndef XRE_UBER_BASE_FEATURES
#define XRE_UBER_BASE_FEATURES 0u
#endif

[[vk::binding(15, 1)]] ConstantBuffer<UberBaseMaterialParameters> uberMaterial;
[[vk::binding(1, 1)]] Texture2D<float4> _MainTex;
[[vk::binding(2, 1)]] SamplerState _MainTexSampler;
#if (XRE_UBER_BASE_FEATURES & 1) != 0 && !defined(XRE_UBER_BASE_DEPTH)
[[vk::binding(3, 1)]] Texture2D<float4> _BumpMap;
[[vk::binding(4, 1)]] SamplerState _BumpMapSampler;
#endif
#if (XRE_UBER_BASE_FEATURES & 2) != 0
[[vk::binding(5, 1)]] Texture2D<float4> _AlphaMask;
[[vk::binding(6, 1)]] SamplerState _AlphaMaskSampler;
#endif
#if (XRE_UBER_BASE_FEATURES & 4) != 0 && !defined(XRE_UBER_BASE_DEPTH)
[[vk::binding(7, 1)]] Texture2D<float4> _PBRMetallicMaps;
[[vk::binding(8, 1)]] SamplerState _PBRMetallicMapsSampler;
[[vk::binding(9, 1)]] Texture2D<float4> _PBRSmoothnessMaps;
[[vk::binding(10, 1)]] SamplerState _PBRSmoothnessMapsSampler;
[[vk::binding(11, 1)]] Texture2D<float4> _SpecularMap;
[[vk::binding(12, 1)]] SamplerState _SpecularMapSampler;
#endif
#if (XRE_UBER_BASE_FEATURES & 8) != 0 && !defined(XRE_UBER_BASE_DEPTH)
[[vk::binding(13, 1)]] Texture2D<float4> _EmissionMap;
[[vk::binding(14, 1)]] SamplerState _EmissionMapSampler;
#endif

#ifdef XRE_UBER_BASE_SPECIALIZED
// The offline source generator defines this with the exact prepared static
// literals after including this file. Animated fields remain uniform loads.
UberBaseMaterialParameters uberBaseResolveMaterial();
#else
UberBaseMaterialParameters uberBaseResolveMaterial() { return uberMaterial; }
#endif

struct UberBaseVertexInput
{
    [[vk::location(0)]] float3 position : TEXCOORD0;
    [[vk::location(1)]] float3 normal : TEXCOORD1;
    [[vk::location(2)]] float4 tangent : TEXCOORD2;
    [[vk::location(3)]] float2 uv0 : TEXCOORD3;
    [[vk::location(4)]] float2 uv1 : TEXCOORD4;
    [[vk::location(5)]] float2 uv2 : TEXCOORD5;
    [[vk::location(6)]] float2 uv3 : TEXCOORD6;
    [[vk::location(7)]] float4 color0 : TEXCOORD7;
};

struct UberBaseVertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 worldPosition : TEXCOORD0;
    [[vk::location(1)]] float3 worldNormal : TEXCOORD1;
    [[vk::location(2)]] float3 worldTangent : TEXCOORD2;
    [[vk::location(3)]] float3 worldBitangent : TEXCOORD3;
    [[vk::location(4)]] float3 localPosition : TEXCOORD4;
    [[vk::location(5)]] float2 uv0 : TEXCOORD5;
    [[vk::location(6)]] float2 uv1 : TEXCOORD6;
    [[vk::location(7)]] float2 uv2 : TEXCOORD7;
    [[vk::location(8)]] float2 uv3 : TEXCOORD8;
    [[vk::location(9)]] float4 color0 : TEXCOORD9;
};

float3 uberBaseTransformDirection(float3 direction)
{
    float3 column0 = float3(object.modelMatrix[0][0], object.modelMatrix[1][0], object.modelMatrix[2][0]);
    float3 column1 = float3(object.modelMatrix[0][1], object.modelMatrix[1][1], object.modelMatrix[2][1]);
    float3 column2 = float3(object.modelMatrix[0][2], object.modelMatrix[1][2], object.modelMatrix[2][2]);
    // Canonical uniforms.glsl uses these cofactor columns, including their
    // determinant sign under mirrored models, independently for all bases.
    return cross(column1, column2) * direction.x + cross(column2, column0) * direction.y
        + cross(column0, column1) * direction.z;
}

UberBaseVertexOutput uberBaseVertexCore(UberBaseVertexInput input)
{
    UberBaseVertexOutput output;
    float4 world = mul(object.modelMatrix, float4(input.position, 1.0));
    output.position = mul(view.viewProjection, world);
#ifdef XRE_UBER_BASE_POINT_DEPTH
    output.position.y = -output.position.y;
#endif
    output.worldPosition = world.xyz;
    output.localPosition = input.position;
    output.worldNormal = normalize(uberBaseTransformDirection(input.normal));
    output.worldTangent = dot(input.tangent.xyz, input.tangent.xyz) > 0.0
        ? normalize(uberBaseTransformDirection(input.tangent.xyz)) : float3(0.0);
    output.worldBitangent = dot(input.tangent.xyz, input.tangent.xyz) > 0.0
        ? normalize(uberBaseTransformDirection(cross(input.normal, input.tangent.xyz) * input.tangent.w)) : float3(0.0);
    output.uv0 = input.uv0;
    output.uv1 = input.uv1;
    output.uv2 = input.uv2;
    output.uv3 = input.uv3;
    output.color0 = input.color0;
    return output;
}

UberBaseMesh uberBaseMesh(UberBaseVertexOutput input, bool frontFace)
{
    UberBaseMesh mesh;
    mesh.worldPosition = input.worldPosition;
    mesh.localPosition = input.localPosition;
    mesh.vertexNormal = normalize(input.worldNormal) * (frontFace ? 1.0 : -1.0);
    mesh.worldNormal = mesh.vertexNormal;
    mesh.viewDirection = normalize(view.cameraPosition.xyz - input.worldPosition);
    mesh.tangent = input.worldTangent;
    mesh.bitangent = input.worldBitangent;
    mesh.vertexColor = input.color0;
    mesh.uv0 = input.uv0;
    mesh.uv1 = input.uv1;
    mesh.uv2 = input.uv2;
    mesh.uv3 = input.uv3;
    return mesh;
}

float uberBaseTime()
{
    return (XRE_UBER_BASE_FEATURES & UberBaseRenderTime) != 0u ? view.RenderTime : 0.0;
}

#include "UberBaseFragmentSampling.slang"
