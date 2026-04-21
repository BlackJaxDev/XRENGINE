// =============================================================================
// Uber Shader - Fragment Stage
// =============================================================================
// A single specialization-heavy fragment shader that serves every mesh pass
// in the engine. Pass-specific #defines (set by the C# pipeline when the
// variant is compiled) strip whole feature blocks out of this file so that
// one source can cover:
//
//   * Opaque / cutout / transparent forward shading (stylized or PBR)
//   * Depth+normal prepass (for SSAO / SSR / motion)
//   * Shadow caster pass (depth-only)
//   * Weighted-Blended OIT, Per-Pixel Linked-List OIT, and Depth Peeling
//     for order-independent transparency
//
// Most of the XRENGINE_UBER_DISABLE_* macros below are active for this
// variant, stripping the stylized/rim/matcap/dissolve/etc. blocks out at
// compile time so we only pay for what we use.
// =============================================================================

#version 450 core

#define XRENGINE_UBER_MVP_FRAGMENT 1
#define XRENGINE_UBER_DISABLE_COLOR_ADJUSTMENTS 1
#define XRENGINE_UBER_DISABLE_STYLIZED_SHADING 1
#define XRENGINE_UBER_DISABLE_SHADOW_MASKS 1
#define XRENGINE_UBER_DISABLE_RIM_LIGHTING 1
#define XRENGINE_UBER_DISABLE_ADVANCED_SPECULAR 1
#define XRENGINE_UBER_DISABLE_DETAIL_TEXTURES 1
#define XRENGINE_UBER_DISABLE_OUTLINE 1
#define XRENGINE_UBER_DISABLE_BACKFACE 1
#define XRENGINE_UBER_DISABLE_GLITTER 1
#define XRENGINE_UBER_DISABLE_FLIPBOOK 1
#define XRENGINE_UBER_DISABLE_SUBSURFACE 1
#define XRENGINE_UBER_DISABLE_DISSOLVE 1
#define XRENGINE_UBER_DISABLE_PARALLAX 1

// When materials come from an external import path (glTF/FBX), a handful of
// engine-specific features don't map cleanly, so disable them as well.
#ifdef XRENGINE_UBER_IMPORT_MATERIAL
#define XRENGINE_UBER_DISABLE_MATERIAL_AO 1
#define XRENGINE_UBER_DISABLE_EMISSION 1
#define XRENGINE_UBER_DISABLE_MATCAP 1
#define XRENGINE_UBER_DISABLE_RENDER_TIME 1
#endif

// Shared engine headers:
//   common.glsl   - math helpers, color space conversions, hashes, noise, ...
//   uniforms.glsl - the full uniform block (material params, camera, lights)
#include "common.glsl"
#include "uniforms.glsl"
// common.glsl defines PI; undef before the snippets below to avoid redef warnings.
#undef PI
// #pragma snippet pulls in engine-managed GLSL fragments that provide
// implementations for direct/indirect lighting, AO sampling, and normal
// encoding for the prepass.
#pragma snippet "ForwardLighting"
#pragma snippet "AmbientOcclusionSampling"
#pragma snippet "NormalEncoding"

// Transparency algorithm snippets. These are mutually exclusive with each
// other and with the weighted-blended path which is inlined below.
#if defined(XRENGINE_FORWARD_PPLL)
#pragma snippet "ExactTransparencyPpll"         // per-pixel linked-list OIT
#elif defined(XRENGINE_FORWARD_DEPTH_PEEL)
#pragma snippet "ExactTransparencyDepthPeel"    // depth-peeling OIT
#endif

// ============================================
// Fragment Inputs
// ============================================
// Locations must agree with the vertex stage's out contract. The gaps
// (0,1,4,12,20) are intentional: other slots are reserved for optional
// attributes (tangents, extra UVs, skin weights, ...) that this variant
// doesn't consume.
layout(location = 0)  in vec3 FragPos;        // world-space position
layout(location = 1)  in vec3 FragNorm;       // world-space geometric normal
layout(location = 4)  in vec2 FragUV0;        // primary texture coordinates
layout(location = 12) in vec4 FragColor0;     // per-vertex RGBA color
layout(location = 20) in vec3 FragPosLocal;   // object-space position

// ============================================
// Fragment Output
// ============================================
// Exactly one of these output configurations is active per compilation, based
// on which pass we're being compiled for. The rest of the shader routes its
// final color through XRENGINE_WriteForwardFragment() so it doesn't need to
// know which one is live.
#if defined(XRENGINE_DEPTH_NORMAL_PREPASS)
layout(location = 0) out vec2 Normal;         // octahedral-encoded world normal
#elif defined(XRENGINE_SHADOW_CASTER_PASS)
layout(location = 0) out float Depth;         // depth written into the shadow map
#elif defined(XRENGINE_FORWARD_WEIGHTED_OIT)
layout(location = 0) out vec4 OutAccum;       // sum of premultiplied color*weight
layout(location = 1) out vec4 OutRevealage;   // running 1-alpha product (mul blend)
#else
layout(location = 0) out vec4 FragColor;      // ordinary shaded RGBA
#endif

// -----------------------------------------------------------------------------
// Weighted-Blended OIT (McGuire & Bavoil 2013).
//
// Each transparent fragment contributes to two render targets:
//   OutAccum     += vec4(rgb * alpha, alpha) * weight
//   OutRevealage *= (1 - alpha)     (via destination-mul blending in the FBO)
// and a later resolve pass composites them to approximate back-to-front
// ordering without any per-fragment sorting.
// -----------------------------------------------------------------------------
#if defined(XRENGINE_FORWARD_WEIGHTED_OIT)
// Depth-aware weight: closer fragments (smaller gl_FragCoord.z) get a larger
// weight so they dominate the blend, which keeps the resolve visually
// consistent with traditional back-to-front ordering.
float XRE_ComputeOitWeight(float alpha)
{
    float depthWeight = clamp(1.0 - gl_FragCoord.z * 0.85, 0.05, 1.0);
    return clamp(alpha * (0.25 + depthWeight * depthWeight * 4.0), 1e-2, 8.0);
}

// Writes both MRT outputs used by the weighted-blended resolve pass.
void XRE_WriteWeightedBlendedOit(vec4 shadedColor)
{
    float alpha = clamp(shadedColor.a, 0.0, 1.0);
    // Fully transparent fragments contribute nothing; discard so we don't
    // touch revealage with a no-op and so early-z can still do its job.
    if (alpha <= 0.0001)
        discard;

    float weight = XRE_ComputeOitWeight(alpha);
    vec3 premultiplied = shadedColor.rgb * alpha;
    OutAccum = vec4(premultiplied * weight, alpha * weight);
    OutRevealage = vec4(alpha);
}
#endif

// Called at the top of main() for color passes. For depth peeling, this lets
// the snippet cull fragments that don't belong to the current peel layer.
// Non-peeling builds get a no-op.
void XRENGINE_BeginForwardFragmentOutput()
{
#if defined(XRENGINE_FORWARD_DEPTH_PEEL)
    if (XRE_ShouldDiscardDepthPeelFragment())
        discard;
#endif
}

// Single dispatch point for the final color so the rest of the shader is
// indifferent to which transparency technique this variant uses.
void XRENGINE_WriteForwardFragment(vec4 shadedColor)
{
#if defined(XRENGINE_FORWARD_WEIGHTED_OIT)
    XRE_WriteWeightedBlendedOit(shadedColor);
#elif defined(XRENGINE_FORWARD_PPLL)
    XRE_StorePerPixelLinkedListFragment(shadedColor);
#elif defined(XRENGINE_DEPTH_NORMAL_PREPASS) || defined(XRENGINE_SHADOW_CASTER_PASS)
    // Prepass/shadow outputs were written earlier in main(); nothing to do.
    return;
#else
    FragColor = shadedColor;
#endif
}

// ============================================
// Internal Data Structures
// ============================================
// Plain-old-data bundles passed between helpers. Keeping them grouped means
// feature blocks can read/write everything they need via one parameter,
// which keeps function signatures short and register pressure predictable.

// Geometry + surface info for the current fragment. The name "Toon" is
// historical; the PBR path uses the same struct.
struct ToonMesh {
    vec3 worldNormal;     // final shading normal (after normal mapping)
    vec3 vertexNormal;    // interpolated vertex normal, flipped for back faces
    vec3 worldPos;        // world-space position
    vec3 localPos;        // object-space position (used by local-space effects)
    vec3 viewDir;         // normalized fragment -> camera direction
    vec4 vertexColor;     // interpolated per-vertex color
    vec2 uv[4];           // up to 4 UV channels
    float isFrontFace;    // +1 front-facing, -1 back-facing
    mat3 TBN;             // tangent/bitangent/normal basis for normal maps
};

// Lighting context for the primary (directional) light in the stylized path,
// plus the precomputed dot products lighting helpers reuse.
struct ToonLight {
    vec3 direction;       // unit vector from fragment toward the light
    vec3 color;           // light color * diffuse intensity
    float attenuation;    // distance falloff (1.0 for directional)
    vec3 indirectColor;   // ambient / IBL contribution
    float nDotL;          // saturate(dot(N,L)) not applied here — raw dot
    float nDotV;
    float nDotH;
    float lDotH;
    vec3 halfDir;         // normalize(L + V)
    float lightMap;       // NdotL remapped per the selected lighting mode
    vec3 reflectionDir;   // reflect(-V, N), used by matcap / reflections
    vec3 finalLighting;   // populated by the chosen shading mode
};

// Running composite as the fragment walks through feature blocks.
struct FragmentData {
    vec3 baseColor;       // albedo after color/detail adjustments
    vec3 finalColor;      // lit color being accumulated
    float alpha;
    vec3 emission;        // additive emissive term added on top at the end
};

// Classic PBR inputs for the forward PBR path.
struct PBRData {
    float metallic;
    float roughness;              // Disney/linear roughness (perceptual^2)
    float perceptualRoughness;    // artist-facing roughness in [0,1]
    float reflectionMask;         // IBL strength scalar
    float specularMask;           // direct specular strength scalar
    vec3 F0;                      // normal-incidence Fresnel reflectance
    vec3 diffuseColor;            // albedo * (1 - metallic)
    vec3 specularColor;           // specular lobe tint
};

// -----------------------------------------------------------------------------
// Build a world-space TBN from screen-space derivatives of position and UV.
// This is the Mittring/Schueler "cotangent frame" technique: it lets us
// support normal mapping on meshes that didn't ship with baked tangents.
// If the UV derivatives are degenerate (collapsed UVs), we fall back to an
// arbitrary orthonormal basis around the normal so normal mapping still
// produces sensible results instead of NaNs.
// -----------------------------------------------------------------------------
mat3 computeWorldTbn(vec3 normal, vec3 worldPos, vec2 uv)
{
    vec3 n = normalize(normal);
    vec3 dp1 = dFdx(worldPos);
    vec3 dp2 = dFdy(worldPos);
    vec2 duv1 = dFdx(uv);
    vec2 duv2 = dFdy(uv);

    float det = duv1.x * duv2.y - duv1.y * duv2.x;
    if (abs(det) < 1e-6) {
        // Degenerate UVs -> synthesize any orthonormal frame around n.
        vec3 tangent = normalize(abs(n.z) < 0.999 ? cross(n, vec3(0.0, 0.0, 1.0)) : cross(n, vec3(0.0, 1.0, 0.0)));
        vec3 bitangent = normalize(cross(n, tangent));
        return mat3(tangent, bitangent, n);
    }

    // Solve the 2x2 system that maps UV axes into world space, then lazily
    // orthonormalize via a common inverse-sqrt scale.
    vec3 dp2perp = cross(dp2, n);
    vec3 dp1perp = cross(n, dp1);
    vec3 tangent = dp2perp * duv1.x + dp1perp * duv2.x;
    vec3 bitangent = dp2perp * duv1.y + dp1perp * duv2.y;
    float invMax = inversesqrt(max(dot(tangent, tangent), dot(bitangent, bitangent)));
    return mat3(tangent * invMax, bitangent * invMax, n);
}

// ============================================
// UV Selection Helper
// ============================================
// Many material inputs can pick which UV channel (or synthesized projection)
// they sample from. The integer index selects one of:
//   0-3 : mesh UV sets 0..3
//   4   : panoramic (spherical) based on view direction
//   5   : world XZ projection (good for floors/terrain)
//   6   : polar projection around the world up axis
//   8   : local-space XY (object-anchored)
vec2 getUV(int uvIndex, ToonMesh mesh) {
    switch(uvIndex) {
        case 0: return mesh.uv[0];
        case 1: return mesh.uv[1];
        case 2: return mesh.uv[2];
        case 3: return mesh.uv[3];
        case 4: return panosphereUV(mesh.viewDir, mesh.worldNormal);
        case 5: return mesh.worldPos.xz;
        case 6: return polarUV(mesh.worldNormal);
        case 8: return mesh.localPos.xy;
        default: return mesh.uv[0];
    }
}

// ============================================
// Normal Mapping
// ============================================
// Samples the bump/normal map, decodes it into a tangent-space vector, and
// rotates it into world space via the fragment's TBN. When _BumpScale is
// effectively zero we skip the work entirely and keep the vertex normal.
vec3 calculateNormal(ToonMesh mesh) {
    if (abs(_BumpScale) <= EPSILON) {
        return mesh.vertexNormal;
    }

    // UV helpers:
    //   transformUV -> apply tiling/offset from the _ST (scale/translation) vec4
    //   panUV       -> add a time-based scroll to animate textures
    vec2 normalUV = transformUV(getUV(_BumpMapUV, mesh), _BumpMap_ST);
    normalUV = panUV(normalUV, _BumpMapPan, u_Time);

    vec4 normalTex = texture(_BumpMap, normalUV);
    vec3 tangentNormal = unpackNormal(normalTex, _BumpScale);

    // Rotate from tangent space to world space with the per-fragment TBN.
    vec3 worldNormal = normalize(mesh.TBN * tangentNormal);

    return worldNormal;
}

// ============================================
// Base Color Calculation
// ============================================
// Computes the unshaded albedo (plus alpha) for the fragment: main texture
// multiplied by the material tint, optionally further tinted by interpolated
// vertex colors. Vertex colors can be authored in sRGB or linear space, so
// the material declares which.
vec4 calculateBaseColor(ToonMesh mesh) {
    // Sample main texture
    vec2 mainUV = transformUV(getUV(_MainTexUV, mesh), _MainTex_ST);
    mainUV = panUV(mainUV, _MainTexPan, u_Time);
    
    vec4 mainTex = texture(_MainTex, mainUV);
    vec4 baseColor = mainTex * _Color;
    
    // Apply vertex colors if enabled
    if (_MainVertexColoringEnabled > 0.5) {
        vec4 vertColor = mesh.vertexColor;
        if (_MainVertexColoringLinearSpace > 0.5) {
            vertColor.rgb = sRGBToLinear(vertColor.rgb);
        }
        baseColor.rgb = mix(baseColor.rgb, baseColor.rgb * vertColor.rgb, _MainVertexColoring);
        baseColor.a = mix(baseColor.a, baseColor.a * vertColor.a, _MainUseVertexColorAlpha);
    }
    
    return baseColor;
}

// ============================================
// Color Adjustments
// ============================================
// HSV-style tweaks (saturation, brightness, hue shift) driven by a mask
// texture whose channels gate each effect (R=hue, G=brightness, B=saturation).
// Hue shift can run in classic HSV or in Oklab for a more perceptually uniform
// result. Disabled in this variant.
vec3 applyColorAdjustments(vec3 color, ToonMesh mesh) {
#ifdef XRENGINE_UBER_DISABLE_COLOR_ADJUSTMENTS
    return color;
#else
    if (_MainColorAdjustToggle < 0.5) return color;
    
    // Sample adjustment mask
    vec2 adjustUV = transformUV(getUV(_MainColorAdjustTextureUV, mesh), _MainColorAdjustTexture_ST);
    adjustUV = panUV(adjustUV, _MainColorAdjustTexturePan, u_Time);
    vec4 adjustMask = texture(_MainColorAdjustTexture, adjustUV);
    
    // Saturation adjustment
    float lum = luminance(color);
    color = mix(vec3(lum), color, 1.0 + _Saturation * adjustMask.b);
    
    // Brightness adjustment
    color += _MainBrightness * adjustMask.g;
    
    // Hue shift
    if (_MainHueShiftToggle > 0.5) {
        float hueShiftAmount = _MainHueShift + _MainHueShiftSpeed * u_Time;
        hueShiftAmount *= adjustMask.r;
        
        vec3 shiftedColor;
        if (_MainHueShiftColorSpace == 0) {
            shiftedColor = hueShiftOklab(color, hueShiftAmount);
        } else {
            shiftedColor = hueShift(color, hueShiftAmount);
        }
        
        color = mix(color, shiftedColor, _MainHueShiftReplace);
    }
    
    return color;
#endif
}

// ============================================
// Alpha Handling
// ============================================
// Combines the main texture's alpha with an optional alpha-mask texture
// (replace / multiply / add / subtract), applies a global alpha bias, and
// finally respects a hard "force opaque" override.
float calculateAlpha(vec4 baseColor, ToonMesh mesh) {
    float alpha = baseColor.a;
    
#ifndef XRENGINE_UBER_DISABLE_ALPHA_MASKS
    // Alpha mask
    if (_MainAlphaMaskMode > 0) {
        vec2 alphaMaskUV = transformUV(getUV(_AlphaMaskUV, mesh), _AlphaMask_ST);
        alphaMaskUV = panUV(alphaMaskUV, _AlphaMaskPan, u_Time);
        float alphaMask = texture(_AlphaMask, alphaMaskUV).r;
        
        if (_AlphaMaskInvert > 0.5) {
            alphaMask = 1.0 - alphaMask;
        }
        
        alphaMask = alphaMask * _AlphaMaskBlendStrength + _AlphaMaskValue;
        
        switch(_MainAlphaMaskMode) {
            case 1: alpha = alphaMask; break;                   // Replace
            case 2: alpha *= alphaMask; break;                  // Multiply
            case 3: alpha = saturate(alpha + alphaMask); break; // Add
            case 4: alpha = saturate(alpha - alphaMask); break; // Subtract
        }
    }
#endif
    
    // Apply alpha mod
    alpha = saturate(alpha + _AlphaMod);
    
    // Force opaque if enabled
    if (_AlphaForceOpaque > 0.5) {
        alpha = 1.0;
    }
    
    return alpha;
}

// ============================================
// Forward Lighting Helpers
// ============================================
// Populates a PBRData from the coarse material knobs exposed by this uber
// variant. There's no metallic/roughness map here, so we derive a reasonable
// dielectric default from _SpecularSmoothness. The 0.04 floor keeps the
// surface from becoming a perfect mirror, which would break most BRDF math
// and image-based-lighting filtering.
PBRData buildSurfacePbrData(vec2 uv, vec3 baseColor) {
    PBRData pbr;
    pbr.metallic = 0.0;
    pbr.perceptualRoughness = clamp(1.0 - _SpecularSmoothness, 0.04, 1.0);
    pbr.roughness = pbr.perceptualRoughness * pbr.perceptualRoughness;
    pbr.reflectionMask = 1.0;
    pbr.specularMask = 1.0;
    pbr.F0 = vec3(0.04);             // generic dielectric Fresnel at normal incidence
    pbr.diffuseColor = baseColor;
    pbr.specularColor = vec3(1.0);
    return pbr;
}

float resolveSpecularIntensity(PBRData pbr) {
    return max(_SpecularStrength * pbr.specularMask, 0.0);
}

// ForwardLighting snippet takes an (r, m, s) triple per fragment:
//   r = perceptual roughness, m = metallic, s = direct specular intensity.
vec3 resolveSurfaceRms(PBRData pbr) {
    return vec3(
        clamp(pbr.perceptualRoughness, 0.0, 1.0),
        clamp(pbr.metallic, 0.0, 1.0),
        resolveSpecularIntensity(pbr));
}

// Samples a material-baked AO map (crevices, contact shadows) and lerps it
// toward fully lit by _LightDataAOStrengthR.
float sampleMaterialAmbientOcclusion(ToonMesh mesh) {
#ifdef XRENGINE_UBER_DISABLE_MATERIAL_AO
    return 1.0;
#else
    if (_LightDataAOStrengthR <= 0.0) {
        return 1.0;
    }

    vec2 aoUV = transformUV(getUV(_LightingAOMapsUV, mesh), _LightingAOMaps_ST);
    aoUV = panUV(aoUV, _LightingAOMapsPan, u_Time);
    float aoSample = texture(_LightingAOMaps, aoUV).r;
    return mix(1.0, aoSample, saturate(_LightDataAOStrengthR));
#endif
}

// Indirect / IBL term. Receives the combined screen+material AO so corners
// and baked crevices correctly darken ambient light.
vec3 calculateForwardAmbientLighting(ToonMesh mesh, vec3 baseColor, vec3 normal, PBRData pbr, float ambientOcclusion) {
    return XRENGINE_CalculateAmbientPbr(
        normal,
        mesh.worldPos,
        baseColor,
        mesh.viewDir,
        resolveSurfaceRms(pbr),
        ambientOcclusion);
}

// Sum of all directional light contributions. `skipPrimaryDirectional` lets
// the stylized path own the primary directional (via a toon ramp / cel etc.)
// while still picking up any additional directionals here.
vec3 calculateForwardDirectionalLighting(ToonMesh mesh, vec3 normal, vec3 baseColor, PBRData pbr, bool skipPrimaryDirectional) {
    vec3 totalLight = vec3(0.0);
    vec3 rms = resolveSurfaceRms(pbr);
    int startIndex = skipPrimaryDirectional ? 1 : 0;

    for (int i = startIndex; i < DirLightCount; ++i) {
        totalLight += XRENGINE_CalcDirLight(
            DirectionalLights[i],
            normal,
            mesh.worldPos,
            baseColor,
            rms,
            pbr.F0,
            i == 0);   // only the primary directional samples the sun shadow cascade
    }

    return totalLight;
}

// Point + spot lights. Two dispatch paths:
//   * Forward+ : reads a precomputed per-tile list of visible lights from
//                SSBOs. Scales cleanly to hundreds of onscreen lights.
//   * Classic  : iterates every light in the scene. Fine for small counts.
vec3 calculateForwardLocalLighting(ToonMesh mesh, vec3 normal, vec3 baseColor, PBRData pbr) {
    vec3 totalLight = vec3(0.0);
    vec3 rms = resolveSurfaceRms(pbr);

    if (ForwardPlusEnabled) {
        // Find which screen-space tile we're in, then index into the tile's
        // visible-light list.
        ivec2 tileCoord = ivec2(gl_FragCoord.xy) / ForwardPlusTileSize;
        int tileCountX = (int(ForwardPlusScreenSize.x) + ForwardPlusTileSize - 1) / ForwardPlusTileSize;
        int tileIndex = tileCoord.y * tileCountX + tileCoord.x;
        int baseIndex = tileIndex * ForwardPlusMaxLightsPerTile;

        for (int o = 0; o < ForwardPlusMaxLightsPerTile; ++o) {
            int lightIndex = ForwardPlusVisibleIndices[baseIndex + o];
            if (lightIndex < 0) {
                break; // sentinel: end of this tile's light list
            }

            ForwardPlusLocalLight l = ForwardPlusLocalLights[lightIndex];
            // Color_Type.w encodes the light kind: 0 = point, 1 = spot.
            totalLight += (l.Color_Type.w < 0.5)
                ? XRENGINE_CalcForwardPlusPointLight(l, normal, mesh.worldPos, baseColor, rms, pbr.F0)
                : XRENGINE_CalcForwardPlusSpotLight(l, normal, mesh.worldPos, baseColor, rms, pbr.F0);
        }

        return totalLight;
    }

    // Classic forward fallback: iterate every scene light.
    for (int i = 0; i < PointLightCount; ++i) {
        totalLight += XRENGINE_CalcPointLight(i, PointLights[i], normal, mesh.worldPos, baseColor, rms, pbr.F0);
    }

    for (int i = 0; i < SpotLightCount; ++i) {
        totalLight += XRENGINE_CalcSpotLight(i, SpotLights[i], normal, mesh.worldPos, baseColor, rms, pbr.F0);
    }

    return totalLight;
}

// Convenience: directional + local direct lighting in one call.
vec3 calculateForwardDirectLighting(ToonMesh mesh, vec3 baseColor, vec3 normal, PBRData pbr, bool skipPrimaryDirectional) {
    return calculateForwardDirectionalLighting(mesh, normal, baseColor, pbr, skipPrimaryDirectional)
        + calculateForwardLocalLighting(mesh, normal, baseColor, pbr);
}

// ============================================
// Lighting Calculation (stylized / shared setup)
// ============================================
// Builds the ToonLight context. In the stylized path, it also remaps NdotL
// into a `lightMap` value used to index toon ramps / cel bands. In the PBR
// path the early #ifdef short-circuits the fancy remap and only fills the
// raw dot products.
ToonLight calculateLighting(ToonMesh mesh, vec3 normal, vec3 indirectColor) {
    ToonLight light;

    if (DirLightCount > 0) {
        // Engine convention: DirectionalLights[i].Direction points *from* the
        // light, so flip it to get L (fragment -> light).
        light.direction = normalize(-DirectionalLights[0].Direction);
        light.color = DirectionalLights[0].Base.Color * DirectionalLights[0].Base.DiffuseIntensity;
    } else {
        // No directional light in the scene -> use a harmless default so
        // downstream dot products stay well-defined.
        light.direction = vec3(0.0, 1.0, 0.0);
        light.color = vec3(0.0);
    }
    light.attenuation = 1.0;

#ifdef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    // PBR path: we just need the raw dot products; no ramp remap.
    light.indirectColor = indirectColor;
    light.nDotL = dot(normal, light.direction);
    light.nDotV = dot(normal, mesh.viewDir);
    light.halfDir = normalize(light.direction + mesh.viewDir);
    light.nDotH = dot(normal, light.halfDir);
    light.lDotH = dot(light.direction, light.halfDir);
    light.reflectionDir = reflect(-mesh.viewDir, normal);
    light.lightMap = saturate(light.nDotL);
    return light;
#else
    
    // Stylized path: optional hemisphere ambient tint + ramp-driven remap.
    light.indirectColor = indirectColor;
    if (_LightingIndirectUsesNormals > 0.0) {
        // Simple hemisphere lighting: upward-facing surfaces brighten,
        // downward-facing surfaces darken.
        float upFactor = dot(normal, vec3(0.0, 1.0, 0.0)) * 0.5 + 0.5;
        light.indirectColor *= mix(0.5, 1.0, upFactor);
    }
    
    // Calculate dot products
    light.nDotL = dot(normal, light.direction);
    light.nDotV = dot(normal, mesh.viewDir);
    light.halfDir = normalize(light.direction + mesh.viewDir);
    light.nDotH = dot(normal, light.halfDir);
    light.lDotH = dot(light.direction, light.halfDir);
    light.reflectionDir = reflect(-mesh.viewDir, normal);
    
    // Remap NdotL into a [0,1] light intensity per the selected mode.
    //   0 Poi Custom / 1 Normalized: map [-1,1] -> [0,1] ("half-Lambert" style)
    //   2 Saturated: standard Lambert clamp
    //   3 Shadows-only: always fully lit; only cast shadows darken it
    float lightMap = light.nDotL;
    switch(_LightingMapMode) {
        case 0: lightMap = light.nDotL * 0.5 + 0.5; break;
        case 1: lightMap = light.nDotL * 0.5 + 0.5; break;
        case 2: lightMap = saturate(light.nDotL); break;
        case 3: lightMap = 1.0; break;
    }
    
    light.lightMap = lightMap;
    
    return light;
#endif
}

// ============================================
// Shadow Map Sampling
// ============================================
// Thin wrapper around the directional-shadow snippet; returns 1.0 (fully lit)
// when there is no directional light to sample.
float sampleShadowMap(vec3 worldPos, vec3 normal, float nDotL) {
    if (DirLightCount <= 0)
        return 1.0;
    return XRENGINE_ReadShadowMapDir(worldPos, normal, max(nDotL, 0.0));
}

// ============================================
// Shading Modes (stylized primary light)
// ============================================
// Applies one of several stylized lighting models to the primary directional
// light. If stylized shading is disabled for this variant we fall through to
// a plain Lambert + ambient composition and skip everything below.
vec3 applyShading(vec3 baseColor, ToonLight light, ToonMesh mesh, vec3 normal) {
#ifdef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    return baseColor * (light.color * saturate(light.nDotL) + light.indirectColor);
#else
    // Artist flag to bypass all shading (pure unlit * light color).
    if (_ShadingEnabled < 0.5) {
        return baseColor * (light.color + light.indirectColor);
    }
    
    // Sample directional light shadow map
    float shadowMapFactor = sampleShadowMap(mesh.worldPos, normal, light.nDotL);
    
    vec3 finalLight;
    float shadow = 1.0;
    
    // Each case builds `shadow` (in [0,1]) and `finalLight`, which are then
    // composed together after the switch.
    switch(_LightingMode) {
        case 0: // Texture Ramp
        {
            float rampUV = saturate(light.lightMap + _ShadowOffset);
            vec3 rampColor = texture(_ToonRamp, vec2(rampUV, 0.5)).rgb;
            shadow = luminance(rampColor);
            finalLight = light.color * rampColor;
            break;
        }
        
        case 1: // Multilayer Math
        {
            float border = _ShadowBorder;
            float blur = _ShadowBlur;
            shadow = smoothstep(border - blur, border + blur, light.lightMap);
            
            vec3 shadowColor = _ShadowColor.rgb * _ShadowColor.a;
            finalLight = mix(shadowColor * light.indirectColor, light.color, shadow);
            break;
        }
        
        case 2: // Wrapped
        {
            float wrap = _LightingWrappedWrap;
            float wrappedNDotL = (light.nDotL + wrap) / (1.0 + wrap);
            shadow = smoothstep(_LightingGradientStart, _LightingGradientEnd, wrappedNDotL);
            
            vec3 shadowColor = _LightingShadowColor;
            finalLight = mix(shadowColor * light.indirectColor, light.color, shadow);
            break;
        }
        
        case 5: // Flat
        {
            // Either a hard binary step or the raw remapped lightMap.
            if (_ForceFlatRampedLightmap > 0.5) {
                shadow = step(0.5, light.lightMap);
            } else {
                shadow = light.lightMap;
            }
            finalLight = light.color * shadow + light.indirectColor * (1.0 - shadow);
            break;
        }

        case 6: // Realistic (plain Lambert)
        {
            shadow = saturate(light.nDotL);
            finalLight = light.color * shadow + light.indirectColor;
            break;
        }
        
        default: // Fallback to flat
        {
            shadow = light.lightMap;
            finalLight = light.color * shadow + light.indirectColor;
            break;
        }
    }
    
    // Fold directional shadow-map occlusion into the shadow term.
    shadow *= shadowMapFactor;

    // Artist-friendly softening toward fully lit.
    shadow = mix(1.0, shadow, _ShadowStrength);

    // Colored tint in shadowed regions (e.g. cool blue shadows).
    vec3 shadowTint = mix(_LightingShadowColor, vec3(1.0), shadow);

    // Compose: albedo * light * shadow tint.
    vec3 result = baseColor * finalLight * shadowTint;

    // Optional hard ceiling on lit brightness to keep highlights readable
    // under strong lights.
    if (_LightingCapEnabled > 0.5) {
        float maxBrightness = _LightingCap;
        result = min(result, baseColor * maxBrightness);
    }

    // Enforce a minimum brightness by rescaling luminance — avoids pitch
    // black regions in stylized renders.
    float currentBrightness = luminance(result);
    if (currentBrightness < _LightingMinLightBrightness) {
        result *= _LightingMinLightBrightness / max(currentBrightness, EPSILON);
    }

    // Partial desaturation while preserving lighting detail.
    if (_LightingMonochromatic > 0.0) {
        float gray = luminance(result);
        result = mix(result, baseColor * gray, _LightingMonochromatic);
    }
    
    return result;
#endif
}

// ============================================
// Emission
// ============================================
// Self-illumination: texture * color * strength. Does not interact with
// lighting — it's added on top at the end so it survives tonemap/bloom.
vec3 calculateEmission(ToonMesh mesh, ToonLight light) {
#ifdef XRENGINE_UBER_DISABLE_EMISSION
    return vec3(0.0);
#else
    if (_EnableEmission < 0.5) return vec3(0.0);
    
    vec2 emissionUV = transformUV(getUV(_EmissionMapUV, mesh), _EmissionMap_ST);
    emissionUV = panUV(emissionUV, _EmissionMapPan, u_Time);
    
    // Scrolling emission
    if (_EmissionScrollingEnabled > 0.5) {
        emissionUV += _EmissionScrollingSpeed * u_Time;
    }
    
    vec4 emissionTex = texture(_EmissionMap, emissionUV);
    vec3 emission = emissionTex.rgb * _EmissionColor.rgb * _EmissionStrength;
    
    return emission;
#endif
}

// ============================================
// Matcap
// ============================================
// A "material capture" is a small sphere-rendered texture sampled by the
// view-space normal. It bakes a specific lighting look cheaply and requires
// no runtime light evaluation.
vec3 calculateMatcap(ToonMesh mesh, vec3 normal, ToonLight light, inout vec3 emission) {
#ifdef XRENGINE_UBER_DISABLE_MATCAP
    return vec3(0.0);
#else
    if (_MatcapEnable < 0.5) return vec3(0.0);
    
    // Calculate matcap UV based on view-space normal
    vec3 viewNormal = normalize(mat3(u_ViewMatrix) * normal);
    
    vec2 matcapUV;
    switch(_MatcapUVMode) {
        case 0: // UTS Style
            matcapUV = viewNormal.xy * 0.5 + 0.5;
            break;
        case 1: // Top Pinch
            matcapUV = viewNormal.xy * 0.5 + 0.5;
            matcapUV.y = 1.0 - matcapUV.y; // Flip Y
            break;
        case 2: // Double Sided
            matcapUV = viewNormal.xy * 0.5 + 0.5;
            break;
        default:
            matcapUV = viewNormal.xy * 0.5 + 0.5;
    }
    
    // Apply border
    matcapUV = (matcapUV - 0.5) * (1.0 - _MatcapBorder) + 0.5;
    
    // Sample matcap
    vec4 matcapTex = texture(_Matcap, matcapUV);
    vec3 matcapColor = matcapTex.rgb * _MatcapColor.rgb * _MatcapIntensity;
    
    // Sample mask
    vec2 maskUV = transformUV(mesh.uv[0], _MatcapMask_ST);
    vec4 maskTex = texture(_MatcapMask, maskUV);
    float mask = maskTex[_MatcapMaskChannel];
    if (_MatcapMaskInvert > 0.5) mask = 1.0 - mask;
    
    // Hide in shadow
    mask *= mix(1.0, light.lightMap, _MatcapLightMask);
    
    matcapColor *= mask;
    
    // Add to emission
    emission += matcapColor * _MatcapEmissionStrength;
    
    return matcapColor;
#endif
}

// ============================================
// Rim Lighting
// ============================================
// Fresnel-style edge highlight. Three curve shapes are offered to match the
// common Poiyomi/UTS/LilToon looks familiar to the VRChat avatar community.
vec3 calculateRimLight(ToonMesh mesh, vec3 normal, ToonLight light) {
#ifdef XRENGINE_UBER_DISABLE_RIM_LIGHTING
    return vec3(0.0);
#else
    if (_EnableRimLighting < 0.5) return vec3(0.0);
    
    float nDotV = saturate(dot(normal, mesh.viewDir));
    
    float rim;
    switch(_RimStyle) {
        case 0: // Poiyomi
            rim = 1.0 - nDotV;
            rim = pow(rim, (1.0 - _RimWidth) * 10.0);
            rim = smoothstep(0.0, 1.0 - _RimSharpness, rim);
            break;
        case 1: // UTS2
            rim = pow(1.0 - nDotV, exp2(mix(4.0, 0.0, _RimWidth)));
            break;
        case 2: // LilToon
            rim = saturate((1.0 - nDotV - (1.0 - _RimWidth)) / _RimWidth);
            rim = pow(rim, 1.0 / max(_RimSharpness, EPSILON));
            break;
        default:
            rim = 1.0 - nDotV;
    }
    
    // Sample mask
    vec2 maskUV = transformUV(mesh.uv[0], _RimMask_ST);
    float mask = texture(_RimMask, maskUV)[_RimMaskChannel];
    rim *= mask;
    
    // Hide in shadow
    rim *= mix(1.0, light.lightMap, _RimHideInShadow);
    
    // Apply color
    vec3 rimColor = _RimLightColor.rgb * rim * _RimBlendStrength;
    
    // Mix with light color
    rimColor = mix(rimColor, rimColor * light.color, _RimLightColorBias);
    
    return rimColor;
#endif
}

// ============================================
// Main Fragment Function
// ============================================
// High-level flow:
//   1. Assemble ToonMesh (positions, normal, TBN, UVs, face orientation).
//   2. Depth-peel early-discard if we're on a hidden peel layer.
//   3. Apply parallax (optional) then normal mapping.
//   4. Sample base color + alpha; run color adjustments; test cutout.
//   5. Apply dissolve; on prepass/shadow pass, emit their outputs and exit.
//   6. Detail textures, then ambient + direct lighting (PBR or stylized).
//   7. Back-face override, SSS, matcap, rim, glitter, flipbook, emission.
//   8. Force alpha=1 for opaque/cutout; route through the pass-specific writer.
void main() {
    // ---- 1. Build ToonMesh --------------------------------------------------
    ToonMesh mesh;
    // This variant only streams UV0; mirror it into the other UV slots so
    // features that request a higher channel still get something sensible.
    mesh.uv[0] = FragUV0;
    mesh.uv[1] = FragUV0;
    mesh.uv[2] = FragUV0;
    mesh.uv[3] = FragUV0;
    mesh.worldPos = FragPos;
    mesh.localPos = FragPosLocal;
    mesh.vertexNormal = normalize(FragNorm);
    mesh.vertexColor = FragColor0;
    mesh.viewDir = normalize(u_CameraPosition - FragPos);
    mesh.isFrontFace = gl_FrontFacing ? 1.0 : -1.0;

    // Flip the interpolated normal when shading the back of a double-sided
    // surface (e.g. hair cards) so lighting reads correctly from both sides.
    mesh.vertexNormal *= mesh.isFrontFace;
    mesh.worldNormal = mesh.vertexNormal;

    // TBN built from screen-space derivatives — works even when the mesh has
    // no baked tangents.
    mesh.TBN = computeWorldTbn(mesh.vertexNormal, mesh.worldPos, mesh.uv[0]);

    // ---- 2. Depth-peel gate -------------------------------------------------
#if !defined(XRENGINE_DEPTH_NORMAL_PREPASS) && !defined(XRENGINE_SHADOW_CASTER_PASS)
    XRENGINE_BeginForwardFragmentOutput();
#endif

    // ---- 3. Parallax --------------------------------------------------------
    // Parallax occlusion mapping warps UVs along the view ray through a height
    // map to simulate depth. SPOM (silhouette-preserving) additionally
    // discards fragments whose ray steps off the base UV rectangle so the
    // displaced surface also looks correct at grazing angles.
#ifndef XRENGINE_UBER_DISABLE_PARALLAX
    if (_EnableParallax > 0.5) {
        int parallaxMode = int(_ParallaxMode);
        float parallaxValid = 1.0;
        vec2 parallaxUV = applyParallaxWithValidity(mesh.uv[0], mesh.worldPos, mesh.viewDir, mesh.TBN, parallaxMode, parallaxValid);

        // SPOM: discard when the ray marches outside the base UV [0,1] bounds.
        if (parallaxMode == PARALLAX_SILHOUETTE_OCCLUSION && parallaxValid < 0.5) {
            discard;
        }

        mesh.uv[0] = parallaxUV;
    }
#endif

    // Normal map sampling uses the (possibly parallax-warped) UV0.
    mesh.worldNormal = calculateNormal(mesh);

    // ---- 4. Base color, color adjustments, alpha ----------------------------
    FragmentData fragData;
    fragData.emission = vec3(0.0);

    vec4 baseColor = calculateBaseColor(mesh);
    baseColor.rgb = applyColorAdjustments(baseColor.rgb, mesh);
    fragData.alpha = calculateAlpha(baseColor, mesh);

    // Alpha-test cutout: throw out the fragment before writing anything.
    if (_Mode == BLEND_MODE_CUTOUT) {
        if (fragData.alpha < _Cutoff) {
            discard;
        }
    }

    fragData.baseColor = baseColor.rgb;

    // ---- 5. Dissolve + early prepass/shadow outputs -------------------------
    // Dissolve may rewrite baseColor/emission/alpha and can discard entirely
    // for fully-dissolved fragments.
#ifndef XRENGINE_UBER_DISABLE_DISSOLVE
    if (_EnableDissolve > 0.5) {
        if (applyDissolve(mesh.uv[0], mesh.worldPos, mesh.localPos, fragData.baseColor, fragData.emission, fragData.alpha)) {
            discard;
        }
    }
#endif

    // Cheap outputs for non-color passes. Done *after* cutout/dissolve so
    // shadow maps and the depth/normal prepass respect those discards.
#if defined(XRENGINE_SHADOW_CASTER_PASS)
    Depth = gl_FragCoord.z;
    return;
#elif defined(XRENGINE_DEPTH_NORMAL_PREPASS)
    Normal = XRENGINE_EncodeNormal(mesh.worldNormal);
    return;
#endif

    // ---- 6. Detail textures + lighting --------------------------------------
    // Detail textures run before lighting so shading responds to the detail-
    // modulated albedo rather than tinting an already-lit result.
#ifndef XRENGINE_UBER_DISABLE_DETAIL_TEXTURES
    if (_DetailEnabled > 0.5) {
        vec2 detailUV = transformUV(mesh.uv[0], _DetailTex_ST);
        detailUV = panUV(detailUV, _DetailTexPan, u_Time);
        vec3 detailColor = texture(_DetailTex, detailUV).rgb * _DetailTint + _DetailBrightness;

        vec2 detailMaskUV = transformUV(mesh.uv[0], _DetailMask_ST);
        float detailMask = texture(_DetailMask, detailMaskUV).r;
        float detailStrength = saturate(_DetailTexIntensity * detailMask);

        fragData.baseColor = mix(fragData.baseColor, fragData.baseColor * detailColor, detailStrength);
    }
#endif
    
    // Occlusion = screen-space SSAO * material-baked AO.
    float screenAmbientOcclusion = XRENGINE_SampleAmbientOcclusion();
    float materialAmbientOcclusion = sampleMaterialAmbientOcclusion(mesh);
    float combinedAmbientOcclusion = saturate(screenAmbientOcclusion * materialAmbientOcclusion);
    PBRData surfacePbr = buildSurfacePbrData(mesh.uv[0], fragData.baseColor);
    vec3 ambientLighting = calculateForwardAmbientLighting(mesh, fragData.baseColor, mesh.worldNormal, surfacePbr, combinedAmbientOcclusion);
    ToonLight light = calculateLighting(mesh, mesh.worldNormal, ambientLighting);

    // ---- 7. Feature overlays (back face, SSS, matcap, rim, etc.) ----------
    // Separate material for the back side of double-sided geometry.
#ifndef XRENGINE_UBER_DISABLE_BACKFACE
    if (_EnableBackFace > 0.5) {
        if (mesh.isFrontFace < 0.0) {
            vec4 backTex = texture(_BackFaceTexture, mesh.uv[0]);
            vec3 backColor = mix(_BackFaceColor.rgb, backTex.rgb * _BackFaceColor.rgb, saturate(_BackFaceBlendMode));
            fragData.baseColor = mix(fragData.baseColor, backColor, _BackFaceColor.a);
            fragData.alpha = clamp(fragData.alpha * _BackFaceAlpha, 0.0, 1.0);
            fragData.emission += backColor * _BackFaceEmission;
        }
    }
#endif
    
    // Shading dispatch. PBR always evaluates every light (including the
    // primary directional). The stylized path handles the primary directional
    // via a ramp/cel/wrapped model and adds secondary directionals + all
    // local lights on top.
#ifdef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    fragData.finalColor = calculateForwardDirectLighting(mesh, fragData.baseColor, mesh.worldNormal, surfacePbr, false) + ambientLighting;
#else
    bool useStylizedPrimaryLighting = _ShadingEnabled > 0.5 && _LightingMode != 6;
    if (useStylizedPrimaryLighting) {
        fragData.finalColor = applyShading(fragData.baseColor, light, mesh, mesh.worldNormal);
        fragData.finalColor += calculateForwardDirectLighting(mesh, fragData.baseColor, mesh.worldNormal, surfacePbr, true);
    } else {
        fragData.finalColor = calculateForwardDirectLighting(mesh, fragData.baseColor, mesh.worldNormal, surfacePbr, false) + ambientLighting;
    }
#endif
    
    // Subsurface scattering (wrap-lighting approximation): backlit direction
    // times a view-aligned falloff, modulated by a thickness map. Produces
    // the classic "glow through ears / leaves" look.
#ifndef XRENGINE_UBER_DISABLE_SUBSURFACE
    if (_EnableSSS > 0.5) {
        float backLight = max(0.0, dot(-mesh.worldNormal, light.direction));
        float viewWrap = pow(max(0.0, dot(mesh.viewDir, -light.direction)), max(_SSSPower, 0.001));
        float thickness = texture(_SSSThicknessMap, mesh.uv[0]).r;
        float sssStrength = backLight * viewWrap * _SSSScale * mix(1.0, thickness, 0.5);
        vec3 sss = _SSSColor.rgb * (light.color + light.indirectColor * _SSSAmbient) * sssStrength;
        fragData.finalColor += sss;
    }
#endif
    
    // Matcap compositing — can replace, multiply, and/or add independently.
#ifndef XRENGINE_UBER_DISABLE_MATCAP
    vec3 matcapColor = calculateMatcap(mesh, mesh.worldNormal, light, fragData.emission);

    if (_MatcapEnable > 0.5) {
        fragData.finalColor = mix(fragData.finalColor, matcapColor, _MatcapReplace);
        fragData.finalColor *= mix(vec3(1.0), matcapColor, _MatcapMultiply);
        fragData.finalColor += matcapColor * _MatcapAdd;
    }
#endif
    
    // Rim light. Also contributes to emission so it survives bloom.
#ifndef XRENGINE_UBER_DISABLE_RIM_LIGHTING
    vec3 rimColor = calculateRimLight(mesh, mesh.worldNormal, light);
    fragData.finalColor += rimColor;
    fragData.emission += rimColor * _RimEmission;
#endif

    // Sparkle: hashed noise gated by view angle and an optional mask,
    // scrolling over time. Contributes to both color and emission.
#ifndef XRENGINE_UBER_DISABLE_GLITTER
    if (_EnableGlitter > 0.5) {
        vec2 glitterUV = mesh.uv[0] * max(_GlitterDensity, 0.001);
        float glitterNoise = hash21(floor(glitterUV) + vec2(u_Time * _GlitterSpeed));
        float nDotV = saturate(dot(mesh.worldNormal, mesh.viewDir));
        float angleMask = smoothstep(_GlitterMinAngle, _GlitterMaxAngle, 1.0 - nDotV);
        float glitterMask = texture(_GlitterMask, mesh.uv[0]).r;
        float glitterStrength = pow(glitterNoise, max(_GlitterSize, 0.001)) * _GlitterBrightness * angleMask * glitterMask;
        vec3 glitter = _GlitterColor.rgb * glitterStrength;
        fragData.finalColor += glitter;
        fragData.emission += glitter; // Glitter is emissive
    }
#endif
    
    // Flipbook (sprite-sheet) additive overlay.
#ifndef XRENGINE_UBER_DISABLE_FLIPBOOK
    if (_EnableFlipbook > 0.5 && _FlipbookBlendMode > 0.5) {
        vec4 flipbookColor = simpleFlipbook(
            _FlipbookTexture,
            mesh.uv[0],
            int(max(_FlipbookColumns, 1.0)),
            int(max(_FlipbookRows, 1.0)),
            _FlipbookFrameRate,
            u_Time
        );
        fragData.finalColor += flipbookColor.rgb * flipbookColor.a;
    }
#endif
    
    // Authored emission (texture * color * strength).
#ifndef XRENGINE_UBER_DISABLE_EMISSION
    fragData.emission += calculateEmission(mesh, light);
#endif

    // Final composite: emission is always added on top of lit color so it
    // remains bright through tonemap and bloom.
    fragData.finalColor += fragData.emission;

    // ---- 8. Final write -----------------------------------------------------
    vec4 shadedColor = vec4(fragData.finalColor, fragData.alpha);

    // Opaque and cutout passes must emit alpha=1. Partial alpha in those
    // passes would confuse MSAA resolve and downstream compositing (bloom /
    // tonemap), producing dark halos around cutout edges.
    if (_Mode == BLEND_MODE_OPAQUE || _Mode == BLEND_MODE_CUTOUT) {
        shadedColor.a = 1.0;
    }

    XRENGINE_WriteForwardFragment(shadedColor);
}
