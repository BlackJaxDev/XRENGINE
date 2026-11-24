// Engine-authored compatibility include for translated Unity programs.
// It gives the light color and the lighting structs of Unity's UnityLightingCommon.cginc.
#ifndef UNITY_LIGHTING_COMMON_INCLUDED
#define UNITY_LIGHTING_COMMON_INCLUDED

#include "UnityShaderVariables.cginc"

#define _LightColor0 XR_MainLightColor()
float4 _SpecColor;

struct UnityLight
{
    float3 color;
    float3 dir;
    float ndotl;
};

struct UnityIndirect
{
    float3 diffuse;
    float3 specular;
};

struct UnityGI
{
    UnityLight light;
    UnityIndirect indirect;
};

struct UnityGIInput
{
    UnityLight light;
    float3 worldPos;
    float3 worldViewDir;
    float atten;
    float3 ambient;
    float4 lightmapUV;
    float4 boxMax[2];
    float4 boxMin[2];
    float4 probePosition[2];
    float4 probeHDR[2];
};

#endif // UNITY_LIGHTING_COMMON_INCLUDED
