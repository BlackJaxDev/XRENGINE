#version 450

#if defined(XRENGINE_DEPTH_NORMAL_PREPASS)
layout (location = 0) out vec2 Normal;
#elif defined(XRENGINE_SHADOW_CASTER_PASS) || defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
layout (location = 0) out float Depth;
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
#elif defined(XRENGINE_DEPTH_NORMAL_PREPASS) || defined(XRENGINE_SHADOW_CASTER_PASS) || defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
    return;
#else
    OutColor = shadedColor;
#endif
}

uniform float MatSpecularIntensity;
uniform float MatShininess;
uniform float AlphaCutoff;

uniform vec3 CameraPosition;
#if defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
uniform vec3 LightPos;
uniform float FarPlaneDist;
#endif

uniform sampler2D Texture0; // Albedo (diffuse)
uniform sampler2D Texture1; // Specular map (intensity in R channel)
uniform sampler2D Texture2; // Alpha mask (R channel)

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;
layout (location = 4) in vec2 FragUV0;

#pragma snippet "ForwardLighting"
#pragma snippet "AmbientOcclusionSampling"
#pragma snippet "NormalEncoding"

void main()
{
#if !defined(XRENGINE_DEPTH_NORMAL_PREPASS) && !defined(XRENGINE_SHADOW_CASTER_PASS) && !defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
    XRENGINE_BeginForwardFragmentOutput();
#endif
    // Sample alpha mask first for early discard
    float alphaMask = texture(Texture2, FragUV0).r;
    if (alphaMask < AlphaCutoff)
        discard;

#if defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
    Depth = length(FragPos - LightPos) / FarPlaneDist;
#elif defined(XRENGINE_SHADOW_CASTER_PASS)
    Depth = gl_FragCoord.z;
#else
    vec3 normal = normalize(FragNorm);

#if defined(XRENGINE_DEPTH_NORMAL_PREPASS)
    Normal = XRENGINE_EncodeNormal(normal);
#else
    vec4 texColor = texture(Texture0, FragUV0);
    float AmbientOcclusion = XRENGINE_SampleAmbientOcclusion();

    // Sample specular map (use R channel as intensity)
    float specularMask = texture(Texture1, FragUV0).r;
    float specIntensity = MatSpecularIntensity * specularMask;

    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, texColor.rgb, specIntensity, AmbientOcclusion);

    XRENGINE_WriteForwardFragment(vec4(totalLight, texColor.a * alphaMask));
#endif
#endif
}
