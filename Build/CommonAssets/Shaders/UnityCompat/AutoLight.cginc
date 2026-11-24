// Engine-authored compatibility include for translated Unity programs.
// It gives the attenuation and shadow macros of Unity's AutoLight.cginc.
// The main light is directional with no distance attenuation. Received shadows
// are not sampled yet, so shadow attenuation is 1.
#ifndef AUTOLIGHT_INCLUDED
#define AUTOLIGHT_INCLUDED

#include "UnityShaderVariables.cginc"

#ifndef unityShadowCoord
#define unityShadowCoord float
#define unityShadowCoord2 float2
#define unityShadowCoord3 float3
#define unityShadowCoord4 float4
#define unityShadowCoord4x4 float4x4
#endif

// Light space and light textures of the additive light passes, with the same keyword conditions as Unity.
// The engine does not draw ForwardAdd passes yet, so these values are not set.
#if defined(POINT) || defined(SPOT) || defined(POINT_COOKIE) || defined(DIRECTIONAL_COOKIE)
unityShadowCoord4x4 unity_WorldToLight;
#endif
#if defined(POINT)
sampler2D_float _LightTexture0;
#elif defined(SPOT)
sampler2D _LightTexture0;
sampler2D_float _LightTextureB0;
#elif defined(POINT_COOKIE)
samplerCUBE_float _LightTexture0;
sampler2D_float _LightTextureB0;
#elif defined(DIRECTIONAL_COOKIE)
sampler2D _LightTexture0;
#endif

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
// Unity's macro is a complete statement with its own ';', and many shaders use it without one.
#define UNITY_LIGHT_ATTENUATION(destName, input, worldPos) float destName = 1.0;
#define UNITY_SHADOW_W(depth) 0.0

float UnitySampleShadowmap(float4 shadowCoord) { return 1.0; }
float UnityComputeShadowFadeDistance(float3 worldPosition, float viewZ) { return 0.0; }
float UnityComputeShadowFade(float fadeDistance) { return 0.0; }
float UnityMixRealtimeAndBakedShadows(float realtimeShadow, float bakedShadow, float fade) { return realtimeShadow; }

#endif // AUTOLIGHT_INCLUDED
