// Engine-authored compatibility include for translated Unity programs.
// It gives the declarations of Unity's UnityMetaPass.cginc. The engine does not bake
// lightmaps from translated shaders and skips Meta passes. Other passes of the same
// shader can still include this file, so the declarations must compile.
#ifndef UNITY_META_PASS_INCLUDED
#define UNITY_META_PASS_INCLUDED

#include "UnityCG.cginc"

struct UnityMetaInput
{
    float3 Albedo;
    float3 Emission;
    float3 SpecularColor;
};

float4 UnityMetaVertexPosition(float4 vertex, float2 uv1, float2 uv2, float4 lightmapST, float4 dynamicLightmapST)
{
    return UnityObjectToClipPos(vertex);
}

float4 UnityMetaFragment(UnityMetaInput input)
{
    return float4(input.Albedo, 1.0);
}

#endif // UNITY_META_PASS_INCLUDED
