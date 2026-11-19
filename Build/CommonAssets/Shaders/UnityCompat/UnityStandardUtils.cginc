// Engine-authored compatibility include for translated Unity programs.
// It gives the public helpers of Unity's UnityStandardUtils.cginc.
#ifndef UNITY_STANDARD_UTILS_INCLUDED
#define UNITY_STANDARD_UTILS_INCLUDED

#include "UnityCG.cginc"
#include "UnityStandardConfig.cginc"

float SpecularStrength(float3 specular) { return max(max(specular.r, specular.g), specular.b); }

float3 EnergyConservationBetweenDiffuseAndSpecular(float3 albedo, float3 specColor, out float oneMinusReflectivity)
{
    oneMinusReflectivity = 1.0 - SpecularStrength(specColor);
    return albedo * oneMinusReflectivity;
}

float OneMinusReflectivityFromMetallic(float metallic)
{
    float oneMinusDielectricSpec = unity_ColorSpaceDielectricSpec.a;
    return oneMinusDielectricSpec - metallic * oneMinusDielectricSpec;
}

float3 DiffuseAndSpecularFromMetallic(float3 albedo, float metallic, out float3 specColor, out float oneMinusReflectivity)
{
    specColor = lerp(unity_ColorSpaceDielectricSpec.rgb, albedo, metallic);
    oneMinusReflectivity = OneMinusReflectivityFromMetallic(metallic);
    return albedo * oneMinusReflectivity;
}

float3 PreMultiplyAlpha(float3 diffColor, float alpha, float oneMinusReflectivity, out float outModifiedAlpha)
{
    diffColor *= alpha;
    outModifiedAlpha = 1.0 - oneMinusReflectivity + alpha * oneMinusReflectivity;
    return diffColor;
}

float3 BlendNormals(float3 n1, float3 n2) { return normalize(float3(n1.xy + n2.xy, n1.z * n2.z)); }

float3x3 CreateTangentToWorldPerVertex(float3 normal, float3 tangent, float tangentSign)
{
    float sign = tangentSign * unity_WorldTransformParams.w;
    float3 binormal = cross(normal, tangent) * sign;
    return float3x3(tangent, binormal, normal);
}

float3 ShadeSHPerVertex(float3 normal, float3 ambient) { return ambient; }
float3 ShadeSHPerPixel(float3 normal, float3 ambient, float3 worldPos) { return ambient + ShadeSH9(float4(normal, 1.0)); }

float3 BoxProjectedCubemapDirection(float3 worldRefl, float3 worldPos, float4 cubemapCenter, float4 boxMin, float4 boxMax)
{
    if (cubemapCenter.w <= 0.0)
        return worldRefl;

    float3 direction = normalize(worldRefl);
    float3 maxDistance = (boxMax.xyz - worldPos) / direction;
    float3 minDistance = (boxMin.xyz - worldPos) / direction;
    float3 far = select(direction > 0.0, maxDistance, minDistance);
    float distance = min(min(far.x, far.y), far.z);
    return worldPos - cubemapCenter.xyz + direction * distance;
}

#endif // UNITY_STANDARD_UTILS_INCLUDED
