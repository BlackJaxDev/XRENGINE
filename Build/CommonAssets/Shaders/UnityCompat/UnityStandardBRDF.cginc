// Engine-authored compatibility include for translated Unity programs.
// It gives the public BRDF helpers of Unity's UnityStandardBRDF.cginc. The terms are
// the standard GGX, Smith joint visibility, and Schlick Fresnel formulas.
#ifndef UNITY_STANDARD_BRDF_INCLUDED
#define UNITY_STANDARD_BRDF_INCLUDED

#include "UnityCG.cginc"
#include "UnityStandardConfig.cginc"
#include "UnityLightingCommon.cginc"

float Pow4(float x) { return x * x * x * x; }
float2 Pow4(float2 x) { return x * x * x * x; }
float3 Pow4(float3 x) { return x * x * x * x; }
float4 Pow4(float4 x) { return x * x * x * x; }
float Pow5(float x) { return x * x * x * x * x; }
float2 Pow5(float2 x) { return x * x * x * x * x; }
float3 Pow5(float3 x) { return x * x * x * x * x; }
float4 Pow5(float4 x) { return x * x * x * x * x; }

float DotClamped(float3 a, float3 b) { return saturate(dot(a, b)); }
float LambertTerm(float3 normal, float3 lightDirection) { return DotClamped(normal, lightDirection); }
float BlinnTerm(float3 normal, float3 halfDirection) { return DotClamped(normal, halfDirection); }
float3 Unity_SafeNormalize(float3 value) { return value * rsqrt(max(0.001, dot(value, value))); }

float SmoothnessToPerceptualRoughness(float smoothness) { return 1.0 - smoothness; }
float SmoothnessToRoughness(float smoothness) { return (1.0 - smoothness) * (1.0 - smoothness); }
float PerceptualRoughnessToRoughness(float perceptualRoughness) { return perceptualRoughness * perceptualRoughness; }
float RoughnessToPerceptualRoughness(float roughness) { return sqrt(roughness); }
float PerceptualRoughnessToSpecPower(float perceptualRoughness)
{
    float m = max(1e-4, PerceptualRoughnessToRoughness(perceptualRoughness));
    return max(2.0 / (m * m) - 2.0, 1e-4);
}

float3 FresnelTerm(float3 f0, float cosA) { return f0 + (1.0 - f0) * Pow5(1.0 - cosA); }
float3 FresnelLerp(float3 f0, float3 f90, float cosA) { return lerp(f0, f90, Pow5(1.0 - cosA)); }
float3 FresnelLerpFast(float3 f0, float3 f90, float cosA) { return lerp(f0, f90, Pow4(1.0 - cosA)); }

float DisneyDiffuse(float NdotV, float NdotL, float LdotH, float perceptualRoughness)
{
    float fd90 = 0.5 + 2.0 * LdotH * LdotH * perceptualRoughness;
    float lightScatter = 1.0 + (fd90 - 1.0) * Pow5(1.0 - NdotL);
    float viewScatter = 1.0 + (fd90 - 1.0) * Pow5(1.0 - NdotV);
    return lightScatter * viewScatter;
}

float SmithJointGGXVisibilityTerm(float NdotL, float NdotV, float roughness)
{
    float lambdaV = NdotL * (NdotV * (1.0 - roughness) + roughness);
    float lambdaL = NdotV * (NdotL * (1.0 - roughness) + roughness);
    return 0.5 / (lambdaV + lambdaL + 1e-5);
}

float GGXTerm(float NdotH, float roughness)
{
    float a2 = roughness * roughness;
    float d = (NdotH * a2 - NdotH) * NdotH + 1.0;
    return UNITY_INV_PI * a2 / (d * d + 1e-7);
}

float4 BRDF1_Unity_PBS(
    float3 diffColor, float3 specColor, float oneMinusReflectivity, float smoothness,
    float3 normal, float3 viewDir, UnityLight light, UnityIndirect gi)
{
    float perceptualRoughness = SmoothnessToPerceptualRoughness(smoothness);
    float3 halfDir = Unity_SafeNormalize(light.dir + viewDir);
    float nv = abs(dot(normal, viewDir));
    float nl = saturate(dot(normal, light.dir));
    float nh = saturate(dot(normal, halfDir));
    float lh = saturate(dot(light.dir, halfDir));
    float diffuseTerm = DisneyDiffuse(nv, nl, lh, perceptualRoughness) * nl;
    float roughness = max(PerceptualRoughnessToRoughness(perceptualRoughness), 0.002);
    float specularTerm = SmithJointGGXVisibilityTerm(nl, nv, roughness) * GGXTerm(nh, roughness) * UNITY_PI;
    specularTerm = max(0.0, specularTerm * nl);
    specularTerm *= any(specColor) ? 1.0 : 0.0;
    float surfaceReduction = 1.0 / (roughness * roughness + 1.0);
    float grazingTerm = saturate(smoothness + (1.0 - oneMinusReflectivity));
    float3 color = diffColor * (gi.diffuse + light.color * diffuseTerm)
        + specularTerm * light.color * FresnelTerm(specColor, lh)
        + surfaceReduction * gi.specular * FresnelLerp(specColor, grazingTerm, nv);
    return float4(color, 1.0);
}

#define UNITY_BRDF_PBS BRDF1_Unity_PBS

#endif // UNITY_STANDARD_BRDF_INCLUDED
