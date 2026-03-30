#version 460
#extension GL_OVR_multiview2 : require

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

layout (location = 1) in vec3 FragNorm;
layout (location = 4) in vec2 FragUV0;

uniform sampler2DArray Texture0;

#pragma snippet "NormalEncoding"

void main()
{
#ifdef XRENGINE_DEPTH_NORMAL_PREPASS
    Normal = XRENGINE_EncodeNormal(normalize(FragNorm));
#else
    XRENGINE_BeginForwardFragmentOutput();
    vec2 uv = FragUV0;
    vec3 uvi = vec3(uv, gl_ViewID_OVR);
    XRENGINE_WriteForwardFragment(texture(Texture0, uvi));
#endif
}
