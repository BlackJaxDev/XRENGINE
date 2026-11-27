#version 450

// Composites mirror glass tint, Schlick Fresnel sheen, and directional specular
// highlight over the reflected HDR scene during deepest-out portal unwind.
// Hardware depth writes restore real mirror quad depth and StencilOp.Invert unwinds
// bits 2..6 back to parentNodeId (reaching 0 after depth 1).
#define XR_ADV_VISIBILITY_COUNTER_INDEX 0u
#include "VisibilityInterface.glslinc"

layout(location = 0) in vec3 InWorldPosition;
layout(location = 1) flat in vec3 InParentCameraPos;
layout(location = 2) flat in vec3 InPlaneNormal;
layout(location = 3) flat in vec4 InGlassTint;
layout(location = 4) flat in vec2 InFresnelAndSpecular;

layout(location = 0) out vec4 OutHdrColor;

void main()
{
    vec3 N = InPlaneNormal;
    float normalLenSq = dot(N, N);
    N = normalLenSq > 1.0e-10 ? N * inversesqrt(normalLenSq) : vec3(0.0, 0.0, 1.0);

    vec3 viewDelta = InParentCameraPos - InWorldPosition;
    float viewLenSq = dot(viewDelta, viewDelta);
    vec3 V = viewLenSq > 1.0e-10 ? viewDelta * inversesqrt(viewLenSq) : N;
    if (dot(N, V) < 0.0)
        N = -N;

    float ndotv = clamp(dot(N, V), 0.0, 1.0);
    float fresnelStrength = clamp(InFresnelAndSpecular.x, 0.0, 1.0);
    float specularStrength = clamp(InFresnelAndSpecular.y, 0.0, 1.0);
    float fresnel = fresnelStrength * pow(1.0 - ndotv, 5.0);

    vec3 specularColor = vec3(0.0);
    if (specularStrength > 0.0)
    {
        for (uint i = 0u; i < uint(XR_ADV_Lights.records.length()); ++i)
        {
            XRAdvancedLightRecord light = XR_ADV_LoadLight(i);
            if ((light.flags & 1u) == 0u || light.type != 0u)
                continue;
            vec3 L = normalize(-light.directionAndOuterCone.xyz);
            float ndotl = max(dot(N, L), 0.0);
            if (ndotl > 0.0)
            {
                vec3 H = normalize(L + V);
                float ndoth = max(dot(N, H), 0.0);
                float spec = pow(ndoth, 128.0) * specularStrength * ndotl;
                specularColor += max(light.colorAndIntensity.rgb, vec3(0.0)) *
                    max(light.colorAndIntensity.w, 0.0) * spec;
            }
            break;
        }
    }

    float baseAlpha = clamp(InGlassTint.a, 0.0, 1.0);
    float specAlpha = clamp(max(specularColor.r, max(specularColor.g, specularColor.b)), 0.0, 1.0);
    float combinedAlpha = clamp(baseAlpha + fresnel * (1.0 - baseAlpha) + specAlpha * (1.0 - baseAlpha), 0.0, 1.0);
    vec3 tintRgb = max(InGlassTint.rgb, vec3(0.0));
    vec3 glassColor = combinedAlpha > 1.0e-5
        ? (tintRgb * baseAlpha + mix(tintRgb, vec3(1.0), 0.65) * fresnel + specularColor) / combinedAlpha
        : vec3(0.0);

    OutHdrColor = vec4(max(glassColor, vec3(0.0)), combinedAlpha);
}
