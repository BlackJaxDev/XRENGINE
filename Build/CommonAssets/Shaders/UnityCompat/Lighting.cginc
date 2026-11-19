// Engine-authored compatibility include for translated Unity programs.
// It gives the light variables and lighting structs that Unity's Lighting.cginc exposes.
#ifndef LIGHTING_INCLUDED
#define LIGHTING_INCLUDED

#include "UnityLightingCommon.cginc"
#include "UnityGlobalIllumination.cginc"

// The output of a legacy surface function.
struct SurfaceOutput
{
    float3 Albedo;
    float3 Normal;
    float3 Emission;
    float Specular;
    float Gloss;
    float Alpha;
};

#endif // LIGHTING_INCLUDED
