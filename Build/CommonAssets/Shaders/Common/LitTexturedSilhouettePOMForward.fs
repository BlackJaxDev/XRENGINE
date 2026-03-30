#version 450

#if defined(XRENGINE_DEPTH_NORMAL_PREPASS)
layout (location = 0) out vec2 Normal;
#elif defined(XRENGINE_FORWARD_WEIGHTED_OIT)
layout (location = 0) out vec4 OutAccum;
layout (location = 1) out vec4 OutRevealage;
#else
layout (location = 0) out vec4 OutColor;
#endif

#if defined(XRENGINE_FORWARD_PPLL)
#pragma snippet "ExactTransparencyPpll"
#elif defined(XRENGINE_FORWARD_DEPTH_PEEL)
#pragma snippet "ExactTransparencyDepthPeel"
#endif

#if defined(XRENGINE_FORWARD_WEIGHTED_OIT)
float XRE_ComputeOitWeight(float alpha)
{
    float depthWeight = clamp(1.0 - gl_FragCoord.z * 0.85, 0.05, 1.0);
    return clamp(alpha * (0.25 + depthWeight * depthWeight * 4.0), 1e-2, 8.0);
}

void XRE_WriteWeightedBlendedOit(vec4 shadedColor)
{
    float alpha = clamp(shadedColor.a, 0.0, 1.0);
    if (alpha <= 0.0001)
        discard;

    float weight = XRE_ComputeOitWeight(alpha);
    vec3 premultiplied = shadedColor.rgb * alpha;
    OutAccum = vec4(premultiplied * weight, alpha * weight);
    OutRevealage = vec4(alpha);
}
#endif

void XRENGINE_BeginForwardFragmentOutput()
{
#if defined(XRENGINE_FORWARD_DEPTH_PEEL)
    if (XRE_ShouldDiscardDepthPeelFragment())
        discard;
#endif
}

void XRENGINE_WriteForwardFragment(vec4 shadedColor)
{
#if defined(XRENGINE_FORWARD_WEIGHTED_OIT)
    XRE_WriteWeightedBlendedOit(shadedColor);
#elif defined(XRENGINE_FORWARD_PPLL)
    XRE_StorePerPixelLinkedListFragment(shadedColor);
#elif defined(XRENGINE_DEPTH_NORMAL_PREPASS) || defined(XRENGINE_SHADOW_CASTER_PASS)
    return;
#else
    OutColor = shadedColor;
#endif
}

uniform float MatSpecularIntensity;
uniform float MatShininess;

uniform vec3 CameraPosition;

uniform sampler2D Texture0; // Albedo
uniform sampler2D Texture1; // Height (R)

// Parallax (silhouette POM) uniforms
uniform float ParallaxScale;        // UV units
uniform int ParallaxMinSteps;
uniform int ParallaxMaxSteps;
uniform int ParallaxRefineSteps;    // binary refinement steps
uniform float ParallaxHeightBias;   // applied to height before inversion
uniform float ParallaxSilhouette;   // >0.5 enables discard when UV exits [0,1]

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;
layout (location = 4) in vec2 FragUV0;

#pragma snippet "ForwardLighting"
#pragma snippet "AmbientOcclusionSampling"
#pragma snippet "ParallaxMapping"
#pragma snippet "NormalEncoding"

void main()
{
#ifndef XRENGINE_DEPTH_NORMAL_PREPASS
    XRENGINE_BeginForwardFragmentOutput();
#endif
    vec3 normal = normalize(FragNorm);
    vec3 viewDirWS = normalize(CameraPosition - FragPos);

    mat3 tbn = XRENGINE_ComputeTBN(normal, FragPos, FragUV0);
    vec3 viewDirTS = transpose(tbn) * viewDirWS;

    bool pomValid;
    vec2 uv = XRENGINE_SilhouetteParallaxOcclusionMapping(
        Texture1,
        FragUV0,
        viewDirTS,
        ParallaxScale,
        ParallaxMinSteps,
        ParallaxMaxSteps,
        ParallaxRefineSteps,
        ParallaxHeightBias,
        pomValid);

    if (ParallaxSilhouette > 0.5 && !pomValid)
        discard;

    if (ParallaxSilhouette <= 0.5 && !pomValid)
        uv = FragUV0;

#ifdef XRENGINE_DEPTH_NORMAL_PREPASS
    Normal = XRENGINE_EncodeNormal(normal);
#else
    vec4 texColor = texture(Texture0, uv);
    float AmbientOcclusion = XRENGINE_SampleAmbientOcclusion();

    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, texColor.rgb, MatSpecularIntensity, AmbientOcclusion);

    XRENGINE_WriteForwardFragment(vec4(totalLight, texColor.a));
#endif
}
