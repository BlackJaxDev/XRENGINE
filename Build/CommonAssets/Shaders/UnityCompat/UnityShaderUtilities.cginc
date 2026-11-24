// Engine-authored compatibility include for translated Unity programs.
// It gives the object-to-clip transforms of Unity's UnityShaderUtilities.cginc.
#ifndef UNITY_SHADER_UTILITIES_INCLUDED
#define UNITY_SHADER_UTILITIES_INCLUDED

#include "UnityShaderVariables.cginc"

float4 UnityObjectToClipPos(float3 position)
{
    return mul(UNITY_MATRIX_VP, mul(unity_ObjectToWorld, float4(position, 1.0)));
}

float4 UnityObjectToClipPos(float4 position)
{
    return UnityObjectToClipPos(position.xyz);
}

#endif // UNITY_SHADER_UTILITIES_INCLUDED
