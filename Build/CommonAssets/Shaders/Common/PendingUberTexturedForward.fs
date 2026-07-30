#version 460

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

uniform sampler2D Texture0;
uniform vec4 FallbackBaseColor;
uniform float AlphaCutoff;
uniform bool FallbackHasTexture;
uniform bool FallbackForceOpaque;
uniform bool FallbackUseAlphaCutoff;

#pragma snippet "NormalEncoding"

void main()
{
    vec3 normal = normalize(FragNorm);

#ifdef XRENGINE_DEPTH_NORMAL_PREPASS
    Normal = XRENGINE_EncodeNormal(normal);
#else
    XRENGINE_BeginForwardFragmentOutput();

    vec4 surface = FallbackBaseColor;
    if (FallbackHasTexture)
        surface *= texture(Texture0, FragUV0);

    if (FallbackForceOpaque)
        surface.a = 1.0;
    else if (FallbackUseAlphaCutoff && surface.a < AlphaCutoff)
        discard;

    if (!gl_FrontFacing)
        normal = -normal;

    // Keep the pending-material preview compact and deterministic. This neutral
    // studio key/fill shows albedo and shape while the full Uber lighting shader
    // compiles asynchronously, without expanding the large ForwardLighting snippet.
    const vec3 keyDirection = vec3(0.3289, 0.7894, 0.5181);
    const vec3 fillDirection = vec3(-0.8427, 0.2408, 0.4815);
    float keyLight = max(dot(normal, keyDirection), 0.0);
    float fillLight = max(dot(normal, fillDirection), 0.0);
    float hemisphere = clamp(normal.y * 0.5 + 0.5, 0.0, 1.0);
    float lighting = clamp(0.48 + keyLight * 0.34 + fillLight * 0.10 + hemisphere * 0.08, 0.35, 1.0);
    surface.rgb *= lighting;

    XRENGINE_WriteForwardFragment(surface);
#endif
}
