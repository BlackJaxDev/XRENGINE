// Engine-authored compatibility include for translated Unity programs.
// It gives the attenuation and shadow macros of Unity's AutoLight.cginc.
// The main light is directional with no distance attenuation. Received shadows
// are not sampled yet, so shadow attenuation is 1.
#ifndef AUTOLIGHT_INCLUDED
#define AUTOLIGHT_INCLUDED

#include "UnityShaderVariables.cginc"

#define SHADOW_COORDS(idx1)
#define UNITY_SHADOW_COORDS(idx1)
#define TRANSFER_SHADOW(a)
#define UNITY_TRANSFER_SHADOW(a, coord)
#define SHADOW_ATTENUATION(a) 1.0
#define UNITY_SHADOW_ATTENUATION(a, worldPos) 1.0
#define UNITY_READ_SHADOW_COORDS(input) 0
#define LIGHTING_COORDS(idx1, idx2)
#define UNITY_LIGHTING_COORDS(idx1, idx2)
#define DECLARE_LIGHT_COORDS(idx)
#define COMPUTE_LIGHT_COORDS(a)
#define TRANSFER_VERTEX_TO_FRAGMENT(a)
#define UNITY_TRANSFER_LIGHTING(a, coord)
#define LIGHT_ATTENUATION(a) 1.0
#define UNITY_LIGHT_ATTENUATION(destName, input, worldPos) float destName = 1.0
#define UNITY_SHADOW_W(depth) 0.0

float UnitySampleShadowmap(float4 shadowCoord) { return 1.0; }
float UnityComputeShadowFadeDistance(float3 worldPosition, float viewZ) { return 0.0; }
float UnityComputeShadowFade(float fadeDistance) { return 0.0; }
float UnityMixRealtimeAndBakedShadows(float realtimeShadow, float bakedShadow, float fade) { return realtimeShadow; }

#endif // AUTOLIGHT_INCLUDED
