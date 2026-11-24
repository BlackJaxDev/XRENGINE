// Engine-authored compatibility include for translated Unity programs.
// It gives the reflection probe helpers of Unity's UnityImageBasedLighting.cginc.
#ifndef UNITY_IMAGE_BASED_LIGHTING_INCLUDED
#define UNITY_IMAGE_BASED_LIGHTING_INCLUDED

#include "UnityStandardBRDF.cginc"

struct Unity_GlossyEnvironmentData
{
    float roughness;
    float3 reflUVW;
};

float perceptualRoughnessToMipmapLevel(float perceptualRoughness)
{
    return perceptualRoughness * (1.7 - 0.7 * perceptualRoughness) * UNITY_SPECCUBE_LOD_STEPS;
}

Unity_GlossyEnvironmentData UnityGlossyEnvironmentSetup(float smoothness, float3 worldViewDir, float3 normal, float3 fresnel0)
{
    Unity_GlossyEnvironmentData data;
    data.roughness = SmoothnessToPerceptualRoughness(smoothness);
    data.reflUVW = reflect(-worldViewDir, normal);
    return data;
}

float3 Unity_GlossyEnvironment(TextureCube cube, SamplerState cubeSampler, float4 hdr, Unity_GlossyEnvironmentData glossIn)
{
    float mip = perceptualRoughnessToMipmapLevel(glossIn.roughness);
    float4 sample = cube.SampleLevel(cubeSampler, glossIn.reflUVW, mip);
    return DecodeHDR(sample, hdr);
}

#define UNITY_PASS_TEXCUBE_GLOSSY(tex) tex, sampler##tex

#endif // UNITY_IMAGE_BASED_LIGHTING_INCLUDED
