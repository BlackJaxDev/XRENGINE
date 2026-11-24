// Engine-authored compatibility include for translated Unity programs.
// It gives the physically based lighting entry points of Unity's UnityPBSLighting.cginc.
#ifndef UNITY_PBS_LIGHTING_INCLUDED
#define UNITY_PBS_LIGHTING_INCLUDED

#include "UnityShaderVariables.cginc"
#include "UnityStandardConfig.cginc"
#include "UnityLightingCommon.cginc"
#include "UnityGlobalIllumination.cginc"

struct SurfaceOutputStandard
{
    float3 Albedo;
    float3 Normal;
    float3 Emission;
    float Metallic;
    float Smoothness;
    float Occlusion;
    float Alpha;
};

struct SurfaceOutputStandardSpecular
{
    float3 Albedo;
    float3 Specular;
    float3 Normal;
    float3 Emission;
    float Smoothness;
    float Occlusion;
    float Alpha;
};

// Returns the diffuse color for the output alpha. With _ALPHAPREMULTIPLY_ON, Unity premultiplies the diffuse color.
float3 XR_ApplySurfaceAlpha(float3 diffColor, float alpha, float oneMinusReflectivity, out float outputAlpha)
{
#if defined(_ALPHAPREMULTIPLY_ON)
    return PreMultiplyAlpha(diffColor, alpha, oneMinusReflectivity, outputAlpha);
#else
    outputAlpha = alpha;
    return diffColor;
#endif
}

float4 LightingStandard(SurfaceOutputStandard s, float3 viewDir, UnityGI gi)
{
    float3 normal = normalize(s.Normal);
    float3 specColor;
    float oneMinusReflectivity;
    float3 diffColor = DiffuseAndSpecularFromMetallic(s.Albedo, s.Metallic, specColor, oneMinusReflectivity);
    float outputAlpha;
    diffColor = XR_ApplySurfaceAlpha(diffColor, s.Alpha, oneMinusReflectivity, outputAlpha);
    float4 color = UNITY_BRDF_PBS(diffColor, specColor, oneMinusReflectivity, s.Smoothness, normal, viewDir, gi.light, gi.indirect);
    color.a = outputAlpha;
    return color;
}

void LightingStandard_GI(SurfaceOutputStandard s, UnityGIInput data, inout UnityGI gi)
{
    float3 fresnel0 = lerp(unity_ColorSpaceDielectricSpec.rgb, s.Albedo, s.Metallic);
    Unity_GlossyEnvironmentData glossIn = UnityGlossyEnvironmentSetup(s.Smoothness, data.worldViewDir, s.Normal, fresnel0);
    gi = UnityGlobalIllumination(data, s.Occlusion, s.Normal, glossIn);
}

float4 LightingStandardSpecular(SurfaceOutputStandardSpecular s, float3 viewDir, UnityGI gi)
{
    float3 normal = normalize(s.Normal);
    float oneMinusReflectivity;
    float3 diffColor = EnergyConservationBetweenDiffuseAndSpecular(s.Albedo, s.Specular, oneMinusReflectivity);
    float outputAlpha;
    diffColor = XR_ApplySurfaceAlpha(diffColor, s.Alpha, oneMinusReflectivity, outputAlpha);
    float4 color = UNITY_BRDF_PBS(diffColor, s.Specular, oneMinusReflectivity, s.Smoothness, normal, viewDir, gi.light, gi.indirect);
    color.a = outputAlpha;
    return color;
}

void LightingStandardSpecular_GI(SurfaceOutputStandardSpecular s, UnityGIInput data, inout UnityGI gi)
{
    Unity_GlossyEnvironmentData glossIn = UnityGlossyEnvironmentSetup(s.Smoothness, data.worldViewDir, s.Normal, s.Specular);
    gi = UnityGlobalIllumination(data, s.Occlusion, s.Normal, glossIn);
}

#endif // UNITY_PBS_LIGHTING_INCLUDED
