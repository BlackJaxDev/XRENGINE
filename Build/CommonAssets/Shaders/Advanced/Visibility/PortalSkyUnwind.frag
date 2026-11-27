#version 450

// Draws reflected sky and environment radiance onto uncovered far-depth pixels
// inside a carved portal or mirror during deepest-out portal unwind.
// Runs with DepthCompare = Equal at farDepth and Stencil == (nodeId << 2) on mask 0x7C.
#define XR_ADV_VISIBILITY_COUNTER_INDEX 0u
#include "VisibilityInterface.glslinc"

layout(location = 0) flat in uint PortalViewIndex;
layout(location = 0) out vec4 OutHdrColor;

vec3 XR_ADV_EvaluatePortalSkyAmbient(uint maskViewIndex)
{
    vec3 ambient = vec3(0.0);
    for (uint index = 0u; index < uint(XR_ADV_Environments.records.length()); ++index)
    {
        XRAdvancedEnvironmentRecord env = XR_ADV_LoadEnvironment(index);
        if ((env.flags & 1u) == 0u)
            continue;
        if ((env.viewMaskLo != 0u || env.viewMaskHi != 0u) &&
            (maskViewIndex < 32u ? (env.viewMaskLo & (1u << maskViewIndex)) == 0u :
                (maskViewIndex >= 64u || (env.viewMaskHi & (1u << (maskViewIndex - 32u))) == 0u)))
            continue;
        vec4 val = env.ambientColorAndIntensity;
        if (!any(isnan(val)) && !any(isinf(val)))
            ambient += max(val.rgb, vec3(0.0)) * max(val.a, 0.0);
    }
    return ambient;
}

void main()
{
    if (PortalViewIndex >= uint(XR_ADV_Views.records.length()))
    {
        OutHdrColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    XRAdvancedViewRecord view = XR_ADV_LoadView(PortalViewIndex);
    vec2 renderSize = max(view.renderSizeAndInverse.xy, vec2(1.0));
    vec2 fbUv = gl_FragCoord.xy / renderSize;
    vec2 ndc = XR_ADV_FramebufferUvToClipNdc(view, fbUv);
    float farClipZ = (view.flags & 2u) != 0u ? 0.0 : 1.0;
    vec4 worldFar = XR_ADV_TransformRowVector(vec4(ndc, farClipZ, 1.0), view.inverseViewProjectionJittered);
    vec3 rayDir = vec3(0.0, 1.0, 0.0);
    if (abs(worldFar.w) > 1.0e-6)
    {
        vec3 delta = worldFar.xyz / worldFar.w - view.cameraPositionAndNear.xyz;
        float lenSq = dot(delta, delta);
        if (lenSq > 1.0e-10)
            rayDir = delta * inversesqrt(lenSq);
    }

    uint maskViewIndex = XR_ADV_ViewMaskIndex(view, PortalViewIndex);
    vec3 ambient = XR_ADV_EvaluatePortalSkyAmbient(maskViewIndex);
    vec3 horizon = max(ambient * 1.15, vec3(0.04, 0.05, 0.07));
    vec3 zenith = max(ambient * 0.75 + vec3(0.05, 0.11, 0.22), horizon);
    vec3 ground = max(ambient * 0.35, vec3(0.02, 0.02, 0.025));

    float upFactor = clamp(rayDir.y, -1.0, 1.0);
    vec3 sky = upFactor >= 0.0
        ? mix(horizon, zenith, pow(upFactor, 0.6))
        : mix(horizon, ground, pow(-upFactor, 0.7));

    // Add a subtle directional sun disc/glow from the first active directional light.
    for (uint i = 0u; i < uint(XR_ADV_Lights.records.length()); ++i)
    {
        XRAdvancedLightRecord light = XR_ADV_LoadLight(i);
        if ((light.flags & 1u) == 0u || light.type != 0u)
            continue;
        vec3 sunDir = normalize(-light.directionAndOuterCone.xyz);
        float sunDot = max(dot(rayDir, sunDir), 0.0);
        vec3 sunRadiance = max(light.colorAndIntensity.rgb, vec3(0.0)) * max(light.colorAndIntensity.w, 0.0);
        sky += sunRadiance * (pow(sunDot, 32.0) * 0.15 + smoothstep(0.9992, 0.9998, sunDot) * 2.0);
        break;
    }

    OutHdrColor = vec4(max(sky, vec3(0.0)), 1.0);
}
