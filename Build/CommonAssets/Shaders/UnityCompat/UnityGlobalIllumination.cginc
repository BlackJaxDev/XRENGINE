// Engine-authored compatibility include for translated Unity programs.
// It gives the global illumination entry points of Unity's UnityGlobalIllumination.cginc.
// Indirect diffuse uses the engine ambient; indirect specular samples the bound reflection probe.
#ifndef UNITY_GLOBAL_ILLUMINATION_INCLUDED
#define UNITY_GLOBAL_ILLUMINATION_INCLUDED

#include "UnityImageBasedLighting.cginc"
#include "UnityStandardUtils.cginc"

UnityGI UnityGI_Base(UnityGIInput data, float occlusion, float3 normalWorld)
{
    UnityGI gi = (UnityGI)0;
    gi.light = data.light;
    gi.light.color *= data.atten;
    gi.indirect.diffuse = ShadeSHPerPixel(normalWorld, data.ambient, data.worldPos) * occlusion;
    return gi;
}

float3 UnityGI_IndirectSpecular(UnityGIInput data, float occlusion, Unity_GlossyEnvironmentData glossIn)
{
    return Unity_GlossyEnvironment(unity_SpecCube0, samplerunity_SpecCube0, data.probeHDR[0], glossIn) * occlusion;
}

UnityGI UnityGlobalIllumination(UnityGIInput data, float occlusion, float3 normalWorld)
{
    return UnityGI_Base(data, occlusion, normalWorld);
}

UnityGI UnityGlobalIllumination(UnityGIInput data, float occlusion, float3 normalWorld, Unity_GlossyEnvironmentData glossIn)
{
    UnityGI gi = UnityGI_Base(data, occlusion, normalWorld);
    gi.indirect.specular = UnityGI_IndirectSpecular(data, occlusion, glossIn);
    return gi;
}

#endif // UNITY_GLOBAL_ILLUMINATION_INCLUDED
