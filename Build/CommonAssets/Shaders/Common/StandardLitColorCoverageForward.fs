#version 450

#if defined(XRENGINE_DEPTH_NORMAL_PREPASS)
layout (location = 0) out vec2 Normal;
#elif defined(XRENGINE_SHADOW_CASTER_PASS) || defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
layout (location = 0) out vec4 Depth;
#else
layout (location = 0) out vec4 OutColor;
#endif

uniform vec3 BaseColor;
uniform float Opacity = 1.0;
uniform float Specular = 1.0;
// Retained in the authored parameter schema; the material publishes the
// authoritative cutoff together with its effective coverage mode below.
uniform float AlphaCutoff = 0.5;
uniform vec4 StandardLitCoverage; // mask enabled, cutoff, premultiply RGB, reserved

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;

#if defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
uniform vec3 LightPos;
uniform float FarPlaneDist;
#endif

#if defined(XRENGINE_SHADOW_CASTER_PASS) || defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
#pragma snippet "ShadowMomentEncoding"
#elif defined(XRENGINE_DEPTH_NORMAL_PREPASS)
#pragma snippet "NormalEncoding"
#else
uniform vec3 CameraPosition;
uniform vec3 CameraForward;
#pragma snippet "ForwardLighting"
#pragma snippet "AmbientOcclusionSampling"
#endif

void main()
{
    // Evaluate identical coverage before color, normal, and shadow outputs.
    if (StandardLitCoverage.x > 0.5 && Opacity < StandardLitCoverage.y)
        discard;

#if defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
    XRENGINE_WritePointShadowCasterDepth(Depth, FragPos, LightPos, FarPlaneDist);
#elif defined(XRENGINE_SHADOW_CASTER_PASS)
    XRENGINE_WriteShadowCasterDepth(Depth, gl_FragCoord.z);
#else
    vec3 normal = normalize(FragNorm);
#if defined(XRENGINE_DEPTH_NORMAL_PREPASS)
    Normal = XRENGINE_EncodeNormal(normal);
#else
    vec3 totalLight = XRENGINE_CalculateForwardLightingMaterial(
        normal,
        FragPos,
        BaseColor,
        vec3(Roughness, Metallic, Specular),
        Emission,
        XRENGINE_SampleAmbientOcclusion());

    if (StandardLitCoverage.z > 0.5)
        totalLight *= Opacity;

    OutColor = vec4(totalLight, Opacity);
#endif
#endif
}
