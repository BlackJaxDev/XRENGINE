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
// Feature gating is compile-time only. The canonical shader keeps default-off
// feature modules disabled as a safe fallback; UberShaderVariantBuilder strips
// these recognized guards and reinjects the material's authored feature set.
// =============================================================================

#version 450 core

#define XRENGINE_UBER_MVP_FRAGMENT 1

// Canonical fallback safety: raw UberShader.frag can be compiled before a
// per-material variant is adopted. Keep additive/default-off modules stripped
// here so unprepared imported meshes do not render as emissive white.
#define XRENGINE_UBER_DISABLE_ADVANCED_SPECULAR 1
#define XRENGINE_UBER_DISABLE_ALPHA_MASKS 1
#define XRENGINE_UBER_DISABLE_BACKFACE 1
#define XRENGINE_UBER_DISABLE_COLOR_ADJUSTMENTS 1
#define XRENGINE_UBER_DISABLE_DETAIL_TEXTURES 1
#define XRENGINE_UBER_DISABLE_DISSOLVE 1
#define XRENGINE_UBER_DISABLE_EMISSION 1
#define XRENGINE_UBER_DISABLE_FLIPBOOK 1
#define XRENGINE_UBER_DISABLE_GLITTER 1
#define XRENGINE_UBER_DISABLE_MATERIAL_AO 1
#define XRENGINE_UBER_DISABLE_MATCAP 1
#define XRENGINE_UBER_DISABLE_NORMAL_MAP 1
#define XRENGINE_UBER_DISABLE_OUTLINE 1
#define XRENGINE_UBER_DISABLE_PARALLAX 1
#define XRENGINE_UBER_DISABLE_RIM_LIGHTING 1
#define XRENGINE_UBER_DISABLE_SHADOW_MASKS 1
#define XRENGINE_UBER_DISABLE_STYLIZED_SHADING 1
#define XRENGINE_UBER_DISABLE_SUBSURFACE 1
#define XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS 1
#define XRENGINE_UBER_DISABLE_GLOBAL_MASKS_THEMES 1
#define XRENGINE_UBER_DISABLE_ADVANCED_STYLIZED_LIGHTING 1
#define XRENGINE_UBER_DISABLE_ADVANCED_PBR 1
#define XRENGINE_UBER_DISABLE_LAYERED_MATCAP_RIM 1
#define XRENGINE_UBER_DISABLE_LAYERED_DECALS 1
#define XRENGINE_UBER_DISABLE_LAYERED_EMISSION 1
#define XRENGINE_UBER_DISABLE_TEXTURE_ARRAY_FLIPBOOK 1

// Shared engine headers:
//   common.glsl   - math helpers, color space conversions, hashes, noise, ...
//   uniforms.glsl - the full uniform block (material params, camera, lights)
#include "common.glsl"
#include "uniforms.glsl"
#include "source_shading.glsl"
// common.glsl defines PI; undef before the snippets below to avoid redef warnings.
#undef PI
// #pragma snippet pulls in engine-managed GLSL fragments that provide
// implementations for direct/indirect lighting, AO sampling, and normal
// encoding for the prepass.
#ifndef XRENGINE_UBER_DISABLE_FORWARD_LIGHTING
#pragma snippet "ForwardLighting"
#endif
#ifndef XRENGINE_UBER_DISABLE_FORWARD_AMBIENT_OCCLUSION
#pragma snippet "AmbientOcclusionSampling"
#endif
#pragma snippet "NormalEncoding"
#pragma snippet "ShadowMomentEncoding"
#pragma snippet "ScreenSpaceUtils"

// Transparency algorithm snippets. These are mutually exclusive with each
// other and with the weighted-blended path which is inlined below.
#if defined(XRENGINE_FORWARD_PPLL)
#pragma snippet "ExactTransparencyPpll"         // per-pixel linked-list OIT
#elif defined(XRENGINE_FORWARD_DEPTH_PEEL)
#pragma snippet "ExactTransparencyDepthPeel"    // depth-peeling OIT
#endif

// Feature helper includes. These files provide helper functions referenced by
// the corresponding feature blocks in main(). Each is gated by the same
// XRENGINE_UBER_DISABLE_* macro that uniforms.glsl uses for the matching
// feature's uniforms so enabling a feature at runtime pulls in both the
// uniforms and the helper implementations together.
#ifndef XRENGINE_UBER_DISABLE_PARALLAX
#include "parallax.glsl"   // applyParallaxWithValidity, PARALLAX_* constants
#endif
#ifndef XRENGINE_UBER_DISABLE_DISSOLVE
#include "dissolve.glsl"   // applyDissolve
#endif
#ifndef XRENGINE_UBER_DISABLE_GLITTER
#include "glitter.glsl"    // hash21
#ifndef XRENGINE_UBER_DISABLE_GLITTER
#define XR_GLITTER_F(name) float(name)
#define XR_GLITTER_I(name) int(name)
#define XR_GLITTER_V2(name) name
#define XR_GLITTER_V4(name) name
#define XR_GLITTER_SAMPLE(uv, dx, dy) textureGrad(_GlitterTexture, clamp(uv, vec2(0.0), vec2(1.0)), dx, dy)
#define XR_GLITTER_FRAGMENT_DERIVATIVES
#include "source_glitter.glsl"
#undef XR_GLITTER_FRAGMENT_DERIVATIVES
#undef XR_GLITTER_SAMPLE
#undef XR_GLITTER_V4
#undef XR_GLITTER_I
#undef XR_GLITTER_F
#undef XR_GLITTER_V2
#endif
#endif
#ifndef XRENGINE_UBER_DISABLE_FLIPBOOK
#include "flipbook.glsl"   // simpleFlipbook
#endif

// ============================================
// Fragment Inputs
// ============================================
// Locations must agree with the vertex stage's out contract. The gaps are
// intentional: other slots are reserved for optional attributes.
layout(location = 0) in vec3 FragPos;         // world-space position
layout(location = 1) in vec3 FragNorm;        // world-space geometric normal
layout(location = 2) in vec3 FragTan;         // world-space tangent
layout(location = 3) in vec3 FragBinorm;      // world-space bitangent
layout(location = 4) in vec2 FragUV0;         // primary texture coordinates
layout(location = 5) in vec2 FragUV1;
layout(location = 6) in vec2 FragUV2;
layout(location = 7) in vec2 FragUV3;
layout(location = 12) in vec4 FragColor0;     // per-vertex RGBA color
layout(location = 20) in vec3 FragPosLocal;   // object-space position
#if defined(XRENGINE_DEPTH_NORMAL_PREPASS)
layout(location = 21) flat in uint FragTransformId;
layout(location = 27) flat in uint FragRenderIdentityId;
#endif

#if defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
uniform vec3 LightPos;
uniform float FarPlaneDist;
#endif

// ============================================
// Fragment Output
// ============================================
// Exactly one of these output configurations is active per compilation, based
// on which pass we're being compiled for. The rest of the shader routes its
// final color through XRENGINE_WriteForwardFragment() so it doesn't need to
// know which one is live.
#if defined(XRENGINE_DEPTH_NORMAL_PREPASS)
layout(location = 0) out vec2 Normal;         // octahedral-encoded world normal
layout(location = 1) out uint TransformId;    // compact pre-pass attachment, main GBuffer ID texture
#elif defined(XRENGINE_SHADOW_CASTER_PASS) || defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
layout(location = 0) out vec4 Depth;          // depth or moments written into the shadow map
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
#elif defined(XRENGINE_DEPTH_NORMAL_PREPASS) || defined(XRENGINE_SHADOW_CASTER_PASS) || defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
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
    // Caller passes a pre-normalized vertex normal; skip re-normalizing.
    vec3 n = normal;
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

mat3 computeImportedOrFallbackWorldTbn(vec3 normal, vec3 tangent, vec3 bitangent, vec3 worldPos, vec2 uv)
{
    vec3 n = normal;
    if (dot(tangent, tangent) <= 1e-8 || dot(bitangent, bitangent) <= 1e-8)
        return computeWorldTbn(n, worldPos, uv);

    vec3 t = tangent - n * dot(n, tangent);
    float tangentLengthSq = dot(t, t);
    if (tangentLengthSq <= 1e-8)
        return computeWorldTbn(n, worldPos, uv);

    t *= inversesqrt(tangentLengthSq);
    float handedness = dot(cross(n, t), bitangent) < 0.0 ? -1.0 : 1.0;
    vec3 b = cross(n, t) * handedness;
    return mat3(t, b, n);
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

#include "surface_extensions.glsl"
#include "extended_effects.glsl"

// ============================================
// Normal Mapping
// ============================================
#ifndef XRENGINE_UBER_DISABLE_NORMAL_MAP
vec3 heightMapToTangentNormal(vec2 uv, float scale)
{
    vec2 texelSize = 1.0 / vec2(textureSize(_BumpMap, 0));
    float tl = texture(_BumpMap, uv + vec2(-texelSize.x, -texelSize.y)).r;
    float t  = texture(_BumpMap, uv + vec2( 0.0,         -texelSize.y)).r;
    float tr = texture(_BumpMap, uv + vec2( texelSize.x, -texelSize.y)).r;
    float l  = texture(_BumpMap, uv + vec2(-texelSize.x,  0.0        )).r;
    float r  = texture(_BumpMap, uv + vec2( texelSize.x,  0.0        )).r;
    float bl = texture(_BumpMap, uv + vec2(-texelSize.x,  texelSize.y)).r;
    float b  = texture(_BumpMap, uv + vec2( 0.0,          texelSize.y)).r;
    float br = texture(_BumpMap, uv + vec2( texelSize.x,  texelSize.y)).r;

    float slopeX = ((tl + 2.0 * l + bl) - (tr + 2.0 * r + br)) * 0.25;
    float slopeY = ((tl + 2.0 * t + tr) - (bl + 2.0 * b + br)) * 0.25;
    return normalize(vec3(vec2(slopeX, slopeY) * scale, 1.0));
}

#endif

// Keep the decoded tangent normal until all normal layers are combined.
vec3 calculateTangentNormal(ToonMesh mesh) {
#ifdef XRENGINE_UBER_DISABLE_NORMAL_MAP
    return vec3(0.0, 0.0, 1.0);
#else
    if (abs(_BumpScale) <= EPSILON) {
        return vec3(0.0, 0.0, 1.0);
    }

    // UV helpers:
    //   transformUV -> apply tiling/offset from the _ST (scale/translation) vec4
    //   panUV       -> add a time-based scroll to animate textures
    vec2 normalUV = transformUV(getUV(_BumpMapUV, mesh), _BumpMap_ST);
    normalUV = panUV(normalUV, _BumpMapPan, u_Time);

    vec3 tangentNormal;
    if (NormalMapMode == 1) {
        float heightScale = abs(HeightMapScale) > EPSILON ? HeightMapScale : 1.0;
        tangentNormal = heightMapToTangentNormal(normalUV, heightScale * _BumpScale);
    } else {
        tangentNormal = unpackNormal(texture(_BumpMap, normalUV), _BumpScale);
        if (_BumpMapFlipGreen != 0) tangentNormal.y = -tangentNormal.y;
    }
    return tangentNormal;
#endif
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
    vec4 mainTex = uberSampleMainTexture(mesh);
    vec4 baseColor = mainTex * vec4(uberResolveThemeColor(_ColorThemeIndex, _Color.rgb), _Color.a);
    
    // Apply vertex colors if enabled
    if (_MainVertexColoringEnabled > 0.5) {
        vec4 vertColor = mesh.vertexColor;
        if (_MainVertexColoringLinearSpace > 0.5) {
            vertColor.rgb = sRGBToLinear(vertColor.rgb);
        }
        // mix(x, x*y, t) == x * mix(1, y, t) — saves a vec3 multiply.
        baseColor.rgb *= mix(vec3(1.0), vertColor.rgb, _MainVertexColoring);
        baseColor.a *= mix(1.0, vertColor.a, _MainUseVertexColorAlpha);
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
    return uberApplyExtendedColorAdjustments(color);
#else
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
#ifndef XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS
    if (_AlphaSource == 1)
        alpha = luminance(baseColor.rgb);
    else if (_AlphaSource == 2)
        alpha = mesh.vertexColor.a;
#endif
    
#ifndef XRENGINE_UBER_DISABLE_ALPHA_MASKS
    // Alpha mask
    if (_MainAlphaMaskMode > 0) {
        vec2 alphaMaskUV = transformUV(getUV(_AlphaMaskUV, mesh), _AlphaMask_ST);
        alphaMaskUV = panUV(alphaMaskUV, _AlphaMaskPan, u_Time);
        vec4 alphaMaskSample = texture(_AlphaMask, alphaMaskUV);
#ifdef XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS
        float alphaMask = alphaMaskSample.r;
#else
        float alphaMask = uberChannel(alphaMaskSample, _AlphaMaskChannel);
#endif
        
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
    
    return uberApplyExtendedAlpha(alpha, mesh);
}

// ============================================
// Forward Lighting Helpers
// ============================================
PBRData buildSurfacePbrData(ToonMesh mesh, vec3 baseColor) {
    PBRData pbr;
    pbr.metallic = 0.0;
    pbr.perceptualRoughness = clamp(1.0 - _SpecularSmoothness, 0.04, 1.0);
    pbr.roughness = pbr.perceptualRoughness * pbr.perceptualRoughness;
    pbr.reflectionMask = 1.0;
    pbr.specularMask = 1.0;
    pbr.F0 = vec3(0.04);
    pbr.diffuseColor = baseColor;
    pbr.specularColor = vec3(1.0);

#ifndef XRENGINE_UBER_DISABLE_ADVANCED_SPECULAR
    vec2 metallicUV = transformUV(getUV(_PBRMetallicMapsUV, mesh), _PBRMetallicMaps_ST);
    metallicUV = panUV(metallicUV, _PBRMetallicMapsPan, u_Time);
    vec4 metallicTex = texture(_PBRMetallicMaps, metallicUV);
    if (_PBRMochiePackedMasksEnabled != 0) {
        float reflectionChannel = _PBRMetallicMapsReflectionMaskChannel == 4
            ? 1.0 : metallicTex[clamp(_PBRMetallicMapsReflectionMaskChannel, 0, 3)];
        float specularChannel = _PBRMetallicMapsSpecularMaskChannel == 4
            ? 1.0 : metallicTex[clamp(_PBRMetallicMapsSpecularMaskChannel, 0, 3)];
        pbr.reflectionMask = reflectionChannel * _PBRReflectionStrength;
        pbr.specularMask = specularChannel * _SpecularStrength;
        if (_PBRReflectionMaskInvert > 0.5) pbr.reflectionMask = 1.0 - pbr.reflectionMask;
        if (_PBRSpecularMaskInvert > 0.5) pbr.specularMask = 1.0 - pbr.specularMask;
    }
    float metallic = metallicTex[clamp(_PBRMetallicMapsMetallicChannel, 0, 3)];
    if (_PBRMetallicMapInvert > 0.5)
        metallic = 1.0 - metallic;

    vec2 smoothnessUV = transformUV(getUV(_PBRSmoothnessMapsUV, mesh), _PBRSmoothnessMaps_ST);
    smoothnessUV = panUV(smoothnessUV, _PBRSmoothnessMapsPan, u_Time);
    vec4 smoothnessTex = texture(_PBRSmoothnessMaps, smoothnessUV);
    float smoothness = smoothnessTex[clamp(_PBRSmoothnessMapsChannel, 0, 3)];
    if (_PBRSmoothnessMapInvert > 0.5)
        smoothness = 1.0 - smoothness;

    pbr.metallic = saturate(metallic * _PBRMetallicMultiplier);
    pbr.perceptualRoughness = clamp(1.0 - smoothness * _PBRRoughnessMultiplier, 0.04, 1.0);
    pbr.roughness = pbr.perceptualRoughness * pbr.perceptualRoughness;
    pbr.F0 = mix(vec3(0.04), baseColor, pbr.metallic);
    pbr.diffuseColor = baseColor * (1.0 - pbr.metallic);
    pbr.specularColor = mix(vec3(1.0), baseColor, pbr.metallic);

    vec4 specTex = texture(_SpecularMap, transformUV(mesh.uv[0], _SpecularMap_ST));
    float specMask = saturate(max(max(specTex.r, specTex.g), max(specTex.b, specTex.a)));
    pbr.specularMask *= specMask;
    pbr.specularColor *= max(specTex.rgb * _SpecularTint.rgb, vec3(0.0));
    pbr.F0 = clamp(pbr.F0 * pbr.specularColor * max(_SpecularTint.a, 0.0), vec3(0.0), vec3(1.0));

    if (_SpecularType == 2)
    {
        pbr.perceptualRoughness = clamp(pbr.perceptualRoughness * 0.75, 0.04, 1.0);
        pbr.roughness = pbr.perceptualRoughness * pbr.perceptualRoughness;
    }
#endif

    return pbr;
}

float resolveSpecularIntensity(PBRData pbr) {
#ifndef XRENGINE_UBER_DISABLE_ADVANCED_SPECULAR
    if (_PBRMochiePackedMasksEnabled != 0)
        return max(pbr.specularMask, 0.0);
#endif
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
    vec2 aoUV = transformUV(getUV(_LightingAOMapsUV, mesh), _LightingAOMaps_ST);
    aoUV = panUV(aoUV, _LightingAOMapsPan, u_Time);
    vec4 aoSample = texture(_LightingAOMaps, aoUV);
#ifdef XRENGINE_UBER_DISABLE_ADVANCED_STYLIZED_LIGHTING
    return mix(1.0, aoSample.r, saturate(_LightDataAOStrengthR));
#else
    vec4 weighted = mix(vec4(1.0), aoSample, saturate(_LightDataAOStrengths));
    return weighted.r * weighted.g * weighted.b * weighted.a;
#endif
#endif
}

float sampleLightingShadowMask(ToonMesh mesh) {
#ifdef XRENGINE_UBER_DISABLE_SHADOW_MASKS
    return 1.0;
#else
    vec2 maskUV = transformUV(mesh.uv[0], _LightingShadowMasks_ST);
    vec4 mask = texture(_LightingShadowMasks, maskUV);
#ifdef XRENGINE_UBER_DISABLE_ADVANCED_STYLIZED_LIGHTING
    return mix(1.0, mask.r, saturate(_LightingShadowMaskStrengthR));
#else
    vec4 weighted = mix(vec4(1.0), mask, saturate(_LightingShadowMaskStrengths));
    vec2 detailUv = panUV(transformUV(getUV(0, mesh), _LightingDetailShadowMap_ST), vec2(0.0), u_Time);
    float detail = mix(1.0, texture(_LightingDetailShadowMap, detailUv).r, saturate(_LightingDetailShadowStrength));
    return weighted.r * weighted.g * weighted.b * weighted.a * detail;
#endif
#endif
}

vec3 applyDetailTangentNormal(ToonMesh mesh, vec3 normal) {
#ifdef XRENGINE_UBER_DISABLE_DETAIL_TEXTURES
    return normal;
#else
    if (abs(_DetailNormalMapScale) <= EPSILON) {
        return normal;
    }

    vec2 detailNormalUV = transformUV(getUV(_DetailNormalMapUV, mesh), _DetailNormalMap_ST);
    detailNormalUV = panUV(detailNormalUV, _DetailNormalMapPan, u_Time);
    vec2 maskUv = panUV(transformUV(getUV(_DetailMaskUV, mesh), _DetailMask_ST), _DetailMaskPan, u_Time);
    float mask = texture(_DetailMask, maskUv).g;
    vec3 detailTangentNormal = unpackNormal(texture(_DetailNormalMap, detailNormalUV), _DetailNormalMapScale * mask);
    if (_DetailNormalMapFlipGreen != 0) detailTangentNormal.y = -detailTangentNormal.y;
    return blendNormals(normal, detailTangentNormal);
#endif
}

// Indirect / IBL term. Receives the combined screen+material AO so corners
// and baked crevices correctly darken ambient light.
vec3 calculateForwardAmbientLightingWithProbe(ToonMesh mesh, vec3 baseColor,
    vec3 normal, PBRData pbr, float ambientOcclusion,
    out vec3 probeDiffuse, out vec3 probeSpecular) {
    vec3 ambientRms = resolveSurfaceRms(pbr);
#ifndef XRENGINE_UBER_DISABLE_ADVANCED_SPECULAR
    if (_PBRMochiePackedMasksEnabled != 0)
        ambientRms.z = 1.0;
#endif
    return XRENGINE_CalculateAmbientPbrWithProbe(
        normal,
        mesh.worldPos,
        baseColor,
        mesh.viewDir,
        ambientRms,
        ambientOcclusion,
        pbr.reflectionMask,
        probeDiffuse,
        probeSpecular);
}

vec3 calculateForwardAmbientLighting(ToonMesh mesh, vec3 baseColor,
    vec3 normal, PBRData pbr, float ambientOcclusion) {
    vec3 probeDiffuse, probeSpecular;
    return calculateForwardAmbientLightingWithProbe(mesh, baseColor,
        normal, pbr, ambientOcclusion, probeDiffuse, probeSpecular);
}

// Sum of all directional light contributions. `skipPrimaryDirectional` lets
// the stylized path own the primary directional (via a toon ramp / cel etc.)
// while still picking up any additional directionals here.
vec3 calculateForwardDirectionalLighting(ToonMesh mesh, vec3 normal, vec3 baseColor, PBRData pbr, bool skipPrimaryDirectional) {
    vec3 totalLight = vec3(0.0);
    vec3 rms = resolveSurfaceRms(pbr);
#ifndef XRENGINE_UBER_DISABLE_ADVANCED_SPECULAR
    if (_SourceMochieSecond.w > 0.5) rms.z = 0.0;
#endif
    int startIndex = skipPrimaryDirectional ? 1 : 0;

    for (int i = startIndex; i < DirLightCount; ++i) {
        DirLight dirLight = DirectionalLights[i];
        vec3 lightDir = normalize(-dirLight.Direction);
        float shadow = 1.0;
        if (i == 0) {
            float geometricNDotL = max(dot(mesh.vertexNormal, lightDir), 0.0);
            shadow = XRENGINE_ReadShadowMapDir(i, dirLight, mesh.worldPos, mesh.vertexNormal, geometricNDotL);
        }

        totalLight += XRENGINE_CalculateDirectPbrLightWithViewDir(
            dirLight.Base.Color,
            dirLight.Base.DiffuseIntensity,
            lightDir,
            normal,
            mesh.worldPos,
            baseColor,
            rms,
            pbr.F0,
            1.0,
            mesh.viewDir) * shadow;
    }

    return totalLight;
}

int getForwardPlusVisibleLightBaseIndex() {
    int tileCountX = ForwardPlusTileCountX;
    int tileCountY = ForwardPlusTileCountY;
    ivec2 tileCoord = XRENGINE_ScreenPixelLocal(gl_FragCoord.xy, ScreenOrigin, vec2(ScreenWidth, ScreenHeight)) / ForwardPlusTileSize;
    tileCoord = clamp(tileCoord, ivec2(0), ivec2(tileCountX - 1, tileCountY - 1));
    int tileIndex = XRENGINE_GetForwardResolvedViewIndex() * (tileCountX * tileCountY) + tileCoord.y * tileCountX + tileCoord.x;
    return tileIndex * ForwardPlusMaxLightsPerTile;
}

// Light selection is shared; PBR and stylized responses keep their own formulas.
struct UberLocalLight {
    bool isPoint;
    int sourceIndex;
    vec3 position;
    vec3 color;
    float diffuseIntensity;
    float radius;
    float brightness;
    vec3 spotDirection;
    float innerCutoff;
    float outerCutoff;
    float exponent;
};

bool trySelectUberLocalLight(int ordinal, int pointCount, int baseIndex, out UberLocalLight selected) {
    selected.spotDirection = vec3(0.0);
    selected.innerCutoff = 0.0;
    selected.outerCutoff = 0.0;
    selected.exponent = 0.0;
    if (ForwardPlusEnabled) {
        int lightIndex = ForwardPlusVisibleIndices[baseIndex + ordinal];
        if (lightIndex < 0) {
            return false;
        }

        ForwardPlusLocalLight light = ForwardPlusLocalLights[lightIndex];
        selected.isPoint = light.Color_Type.w < 0.5;
        selected.sourceIndex = int(light.Params.w + 0.5);
        selected.position = light.PositionWS.xyz;
        selected.color = light.Color_Type.xyz;
        selected.diffuseIntensity = light.Params.z;
        selected.radius = light.Params.x;
        selected.brightness = max(light.Params.y, 0.0001);
        selected.spotDirection = light.DirectionWS_Exponent.xyz;
        selected.innerCutoff = light.SpotAngles.x;
        selected.outerCutoff = light.SpotAngles.y;
        selected.exponent = light.DirectionWS_Exponent.w;
    } else {
        // Keep the regular path's point-then-spot accumulation order.
        selected.isPoint = ordinal < pointCount;
        if (selected.isPoint) {
            selected.sourceIndex = ordinal;
            PointLight light = PointLights[selected.sourceIndex];
            selected.position = light.Position;
            selected.color = light.Base.Color;
            selected.diffuseIntensity = light.Base.DiffuseIntensity;
            selected.radius = light.Radius;
            selected.brightness = light.Brightness;
        } else {
            selected.sourceIndex = ordinal - pointCount;
            SpotLight light = SpotLights[selected.sourceIndex];
            selected.position = light.Base.Position;
            selected.color = light.Base.Base.Color;
            selected.diffuseIntensity = light.Base.Base.DiffuseIntensity;
            selected.radius = light.Base.Radius;
            selected.brightness = light.Base.Brightness;
            selected.spotDirection = light.Direction;
            selected.innerCutoff = light.InnerCutoff;
            selected.outerCutoff = light.OuterCutoff;
            selected.exponent = light.Exponent;
        }
    }

    return true;
}

float calculateStylizedLightMap(float nDotL) {
#ifdef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    return saturate(nDotL);
#else
    float lightMap = nDotL;
    switch(_LightingMapMode) {
        case 0: lightMap = nDotL * 0.5 + 0.5; break;
        case 1: lightMap = saturate(nDotL * 0.5 + 0.5); break;
        case 2: lightMap = saturate(nDotL); break;
        case 3: lightMap = 1.0; break;
    }

    return lightMap;
#endif
}

vec3 resolveStylizedLightingDirection(ToonMesh mesh, vec3 normal, vec3 lightDirection) {
#ifdef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    return lightDirection;
#else
    switch (_LightingDirectionMode) {
        case 1:
            return normal;
        case 2:
            return mesh.viewDir;
        case 3:
            return vec3(0.0, 1.0, 0.0);
        default:
            return lightDirection;
    }
#endif
}

vec3 resolveStylizedLightingColor(vec3 lightColor) {
#ifdef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    return lightColor;
#else
    float maxChannel = max(max(lightColor.r, lightColor.g), lightColor.b);
    switch (_LightingColorMode) {
        case 1:
            return vec3(luminance(lightColor));
        case 2:
            return vec3(maxChannel);
        case 3:
            return maxChannel > EPSILON ? lightColor / maxChannel : vec3(0.0);
        default:
            return lightColor;
    }
#endif
}

#ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
XrSourceMultilayerInputs sourceMultilayerInputs(ToonMesh mesh, vec3 lightDirection);
#endif

ToonLight createToonLight(ToonMesh mesh, vec3 normal, vec3 lightDirection, vec3 lightColor, float attenuation, vec3 indirectColor) {
    ToonLight light;

    vec3 resolvedDirection = resolveStylizedLightingDirection(mesh, normal, lightDirection);
    vec3 safeDirection = dot(resolvedDirection, resolvedDirection) > EPSILON
        ? normalize(resolvedDirection)
        : vec3(0.0, 1.0, 0.0);

    light.direction = safeDirection;
    light.color = (_SourceLightMode != 0 ? lightColor : resolveStylizedLightingColor(lightColor)) * attenuation;
    light.attenuation = attenuation;
    light.indirectColor = indirectColor;

#ifdef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    // PBR path: downstream only reads direction/color/indirectColor/nDotL/
    // lightMap. Skip halfDir/nDotH/lDotH/nDotV/reflectionDir — the
    // ForwardLighting snippet computes its own per-light half-vector.
    light.nDotL = dot(normal, light.direction);
    light.lightMap = saturate(light.nDotL);
    return light;
#else
    // Texture-ramp mode uses this value as a shadow-source blend. Other
    // stylized modes retain the neutral-ambient remap until their native
    // compositions consume that control directly.
    if (_SourceLightMode == 0 && _LightingIgnoreAmbientColor > 0.0 && _LightingMode != 0) {
        vec3 neutralAmbient = vec3(luminance(light.indirectColor));
        light.indirectColor = mix(light.indirectColor, neutralAmbient, saturate(_LightingIgnoreAmbientColor));
    }

    if (_SourceLightMode == 0 && _LightingIndirectUsesNormals > 0.0) {
        // Simple hemisphere lighting: upward-facing surfaces brighten,
        // downward-facing surfaces darken.
        float upFactor = dot(normal, vec3(0.0, 1.0, 0.0)) * 0.5 + 0.5;
        light.indirectColor *= mix(0.5, 1.0, upFactor);
    }

    // Calculate dot products.
    light.nDotL = dot(normal, light.direction);
    light.nDotV = dot(normal, mesh.viewDir);

    vec3 halfVector = light.direction + mesh.viewDir;
    light.halfDir = dot(halfVector, halfVector) > EPSILON
        ? normalize(halfVector)
        : mesh.viewDir;
    light.nDotH = dot(normal, light.halfDir);
    light.lDotH = dot(light.direction, light.halfDir);
    light.reflectionDir = reflect(-mesh.viewDir, normal);

    light.lightMap = calculateStylizedLightMap(light.nDotL);
    return light;
#endif
}

// ============================================
// Lighting Calculation (stylized / shared setup)
// ============================================
// Builds the ToonLight context. In the stylized path, it also remaps NdotL
// into a `lightMap` value used to index toon ramps / cel bands. In the PBR
// path the early #ifdef short-circuits the fancy remap and only fills the
// raw dot products.
XrSourceShCoefficients getCanonicalSourceSh()
{
    XrSourceShCoefficients sh;
#ifdef XRENGINE_UBER_DISABLE_FORWARD_LIGHTING
    sh.ar = sh.ag = sh.ab = sh.br = sh.bg = sh.bb = sh.c = vec4(0.0);
#else
    sh.ar = vec4(0.0, 0.0, 0.0, GlobalAmbient.r);
    sh.ag = vec4(0.0, 0.0, 0.0, GlobalAmbient.g);
    sh.ab = vec4(0.0, 0.0, 0.0, GlobalAmbient.b);
    sh.br = sh.bg = sh.bb = sh.c = vec4(0.0);
    if (SourceSHEnabled) {
        sh.ar = SourceSHAr; sh.ag = SourceSHAg; sh.ab = SourceSHAb;
        sh.br = SourceSHBr; sh.bg = SourceSHBg; sh.bb = SourceSHBb;
        sh.c = SourceSHC;
    }
#endif
    return sh;
}

float sampleShadowMap(ToonMesh mesh);

ToonLight calculateLighting(ToonMesh mesh, vec3 normal, vec3 indirectColor) {
#ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    if (_SourceLightMode != 0) {
        vec3 primary = DirLightCount > 0
            ? DirectionalLights[0].Base.Color * DirectionalLights[0].Base.DiffuseIntensity
            : vec3(0.0);
        XrSourceShCoefficients sh = getCanonicalSourceSh();
        vec3 sourceDirect, sourceIndirect, direction;
        xrSourceShLight(sh, primary, normal,
            DirLightCount > 0 ? normalize(-DirectionalLights[0].Direction)
                : vec3(0.0), _LightingColorMode, _SourceLightSettings.x,
            _LightingIndirectUsesNormals, direction, sourceDirect,
            sourceIndirect);
        vec3 rawSelectedDirect = sourceDirect;
        sourceDirect = xrSourceDirectColor(sourceDirect, _LightingMinLightBrightness,
            _LightingMonochromatic, _LightingCapEnabled, _LightingCap);
        sourceIndirect = xrSourceDirectColor(sourceIndirect, _LightingMinLightBrightness,
            _LightingMonochromatic, _LightingCapEnabled, _LightingCap);
        sourceDirect = max(sourceDirect * _SourceLightSettings.z +
            vec3(_SourceLightSettings.w), vec3(0.0));
        sourceIndirect = max(sourceIndirect * _SourceLightSettings.z +
            vec3(_SourceLightSettings.w), vec3(0.0));
        ToonLight selected = createToonLight(mesh, normal, direction,
            sourceDirect, 1.0, sourceIndirect);
        // Flat shading replaces the light map and does not need shadow filtering here.
        if (_LightingMode == 5) {
            selected.lightMap = _ForceFlatRampedLightmap > 0.5
                ? smoothstep(0.4, 0.6, dot(normal, direction) * 0.5 + 0.5)
                : 1.0;
        } else {
            float visibility = sampleShadowMap(mesh);
            if (_LightingMode == 1) {
                float map = _LightingMapMode == 0
                    ? xrSourceShLightMap(sh, normal, rawSelectedDirect,
                        dot(normal, direction), 1.0, 0.0)
                    : xrSourceLightMap(dot(normal, direction), _LightingMapMode,
                        0.0, 1.0);
                selected.lightMap = xrSourceMultilayerBands(map,
                    vec2(dFdx(map), dFdy(map)), visibility,
                    vec2(dFdx(visibility), dFdy(visibility)),
                    sourceMultilayerInputs(mesh, direction)).x;
            } else if (_LightingMode == 2) {
                selected.lightMap = xrSourceWrappedMap(normal, direction, primary,
                    sh, _LightingWrappedWrap, _LightingWrappedNormalization,
                    sampleMaterialAmbientOcclusion(mesh), visibility,
                    _SourceLightSettings.y, 1.0, _LightingMonochromatic);
            } else {
                selected.lightMap = _LightingMapMode == 0
                    ? xrSourceShLightMap(sh, normal, rawSelectedDirect,
                        dot(normal, direction), visibility, _SourceLightSettings.y)
                    : xrSourceLightMap(dot(normal, direction), _LightingMapMode,
                        _SourceLightSettings.y, visibility);
            }
        }
        return selected;
    }
#endif
#ifndef XRENGINE_UBER_DISABLE_ADVANCED_STYLIZED_LIGHTING
    if (_LightingForceDirection > 0.5 || _LightingForceColor > 0.5) {
        vec3 forcedDirection = _LightingForceDirection > 0.5
            ? normalize(_LightingForcedDirection)
            : (DirLightCount > 0 ? normalize(-DirectionalLights[0].Direction) : vec3(0.0, 1.0, 0.0));
        vec3 forcedColor = _LightingForceColor > 0.5
            ? _LightingForcedColor
            : (DirLightCount > 0
                ? DirectionalLights[0].Base.Color * DirectionalLights[0].Base.DiffuseIntensity
                : vec3(0.0));
        return createToonLight(mesh, normal, forcedDirection, forcedColor, 1.0, indirectColor);
    }
#endif
    if (DirLightCount > 0) {
        // Engine convention: DirectionalLights[i].Direction points *from* the
        // light, so flip it to get L (fragment -> light).
        return createToonLight(
            mesh,
            normal,
            -DirectionalLights[0].Direction,
            DirectionalLights[0].Base.Color * DirectionalLights[0].Base.DiffuseIntensity,
            1.0,
            indirectColor);
    }

    // No directional light in the scene -> use a harmless default so
    // downstream dot products stay well-defined and ambient-only stylized
    // materials still render.
    return createToonLight(mesh, normal, vec3(0.0, 1.0, 0.0), vec3(0.0), 1.0, indirectColor);
}

// ============================================
// Shadow Map Sampling
// ============================================
// Thin wrapper around the directional-shadow snippet; returns 1.0 (fully lit)
// when there is no directional light to sample. Shadow visibility uses the
// geometric receiver normal so normal maps cannot push cascade/contact reads
// into broad false-shadow bands.
float sampleShadowMap(ToonMesh mesh) {
    if (DirLightCount <= 0)
        return 1.0;
    if (_SourceLightMode != 0 && xrSourceLuminance(
        DirectionalLights[0].Base.Color *
        DirectionalLights[0].Base.DiffuseIntensity) <= 0.000001)
        return 1.0;

    vec3 lightDir = normalize(-DirectionalLights[0].Direction);
    float geometricNDotL = max(dot(mesh.vertexNormal, lightDir), 0.0);
    return XRENGINE_ReadShadowMapDir(0, DirectionalLights[0], mesh.worldPos, mesh.vertexNormal, geometricNDotL);
}

// ============================================
// Shading Modes (stylized primary light)
// ============================================
// Applies one of several stylized lighting models to the primary directional
// light. If stylized shading is disabled for this variant we fall through to
// a plain Lambert + ambient composition and skip everything below.
#ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
XrSourceMultilayerInputs sourceMultilayerInputs(ToonMesh mesh, vec3 lightDirection)
{
    XrSourceMultilayerInputs data;
    data.firstColor = _ShadowColor;
    data.secondColor = _Shadow2ndColor;
    data.thirdColor = _Shadow3rdColor;
    data.borderColor = _ShadowBorderColor;
    data.firstTexture = texture(_ShadowColorTex, panUV(transformUV(
        getUV(_ShadowColorTexUV, mesh), _ShadowColorTex_ST),
        _ShadowColorTexPan, u_Time));
    data.secondTexture = texture(_Shadow2ndColorTex, panUV(transformUV(
        getUV(_Shadow2ndColorTexUV, mesh), _Shadow2ndColorTex_ST),
        _Shadow2ndColorTexPan, u_Time));
    data.thirdTexture = texture(_Shadow3rdColorTex, panUV(transformUV(
        getUV(_Shadow3rdColorTexUV, mesh), _Shadow3rdColorTex_ST),
        _Shadow3rdColorTexPan, u_Time));
    data.blurTexture = texture(_MultilayerMathBlurMap, panUV(transformUV(
        getUV(_MultilayerMathBlurMapUV, mesh), _MultilayerMathBlurMap_ST),
        _MultilayerMathBlurMapPan, u_Time));
    data.strengthMask = texture(_ShadowStrengthMask, panUV(transformUV(
        getUV(_ShadowStrengthMaskUV, mesh), _ShadowStrengthMask_ST),
        _ShadowStrengthMaskPan, u_Time)).r;
    data.borderMask = vec4(1.0);
    data.borderMaskX = vec4(1.0);
    data.borderMaskY = vec4(1.0);
    data.options = _SourceMultilayerOptions;
    data.borderShift = _ShadowAOShift;
    data.borderShift2 = _ShadowAOShift2;
    if (data.options.x > 0.5)
    {
        vec2 borderUv = panUV(transformUV(
            getUV(_ShadowBorderMaskUV, mesh), _ShadowBorderMask_ST),
            _ShadowBorderMaskPan, u_Time);
        if (data.options.w > 0.0)
        {
            float minimumGradient = pow(data.options.w, 4.0);
            data.borderMask = textureGrad(_SourceShadowBorderMaskLod, borderUv,
                max(abs(dFdx(borderUv)), vec2(minimumGradient)),
                max(abs(dFdy(borderUv)), vec2(minimumGradient)));
        }
        else
            data.borderMask = texture(_ShadowBorderMask, borderUv);
        data.borderMask.rgb = xrSourceMultilayerRemappedMask(data.borderMask, data);
        data.borderMaskX = data.borderMask + dFdx(data.borderMask);
        data.borderMaskY = data.borderMask + dFdy(data.borderMask);
    }
    data.flatSettings = _SourceMultilayerFlat;
    data.flatLight = dot(u_SourceFlatDirection.xyz, lightDirection);
    data.borders = _SourceMultilayerBorders;
    data.blurs = _SourceMultilayerBlurs;
    data.receive = _SourceMultilayerReceive;
    data.mixSettings = _SourceMultilayerMix;
    return data;
}
#endif

vec3 applyShadingWithShadow(vec3 baseColor, ToonLight light, ToonMesh mesh, vec3 normal, float shadowMapFactor) {
#ifdef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    return baseColor * (light.color * saturate(light.nDotL) + light.indirectColor);
#else
    vec3 finalLight;
    float shadow = 1.0;
    float materialShadowMask = sampleLightingShadowMask(mesh);
    bool usesTextureRampComposition = false;
    vec3 textureRampComposition = vec3(0.0);
    
    // Each case builds `shadow` (in [0,1]) and `finalLight`, which are then
    // composed together after the switch.
    switch(_LightingMode) {
        case 0: // Texture Ramp
        {
            float rampUV = saturate(light.lightMap + _ShadowOffset);
            vec2 rampCoord = vec2(rampUV, getUV(_ToonRampUV, mesh).y);
            rampCoord = transformUV(rampCoord, _ToonRamp_ST);
            rampCoord = panUV(rampCoord, _ToonRampPan, u_Time);
            vec3 rampColor = texture(_ToonRamp, rampCoord).rgb;

            // The texture ramp is a component-wise lit/shadow blend, not
            // merely a tint on direct light. XRENGINE's indirectColor is
            // already surface-colored, so compose in final-color space to
            // avoid multiplying the albedo into ambient a second time.
            vec3 rampedLightMap = mix(
                vec3(1.0),
                rampColor,
                saturate(_ShadowStrength * materialShadowMask));
            vec3 rampShadowColor = _LightingShadowColor * mix(
                light.indirectColor,
                baseColor * rampedLightMap * light.color,
                saturate(_LightingIgnoreAmbientColor));

            textureRampComposition = mix(
                rampShadowColor,
                baseColor * light.color,
                rampedLightMap);
            usesTextureRampComposition = true;
            shadow = luminance(rampedLightMap);
            finalLight = light.color * rampedLightMap;
            break;
        }
        
        case 1: // Multilayer Math
        {
            float border = _ShadowBorder;
            float blur = _ShadowBlur;
            shadow = smoothstep(border - blur, border + blur, light.lightMap);
            
            vec2 firstUV = panUV(transformUV(getUV(_FirstShadeMapUV, mesh), _FirstShadeMap_ST), _FirstShadeMapPan, u_Time);
            vec2 secondUV = panUV(transformUV(getUV(_SecondShadeMapUV, mesh), _SecondShadeMap_ST), _SecondShadeMapPan, u_Time);
            vec4 firstShade = texture(_FirstShadeMap, firstUV);
            vec4 secondShade = texture(_SecondShadeMap, secondUV);
#ifdef XRENGINE_UBER_DISABLE_ADVANCED_STYLIZED_LIGHTING
            float secondBand = smoothstep(border * 0.5 - blur, border * 0.5 + blur, light.lightMap);
            vec3 deepShade = _ShadowColor.rgb * _ShadowColor.a * secondShade.rgb;
#else
            float secondBand = smoothstep(_Shadow2Border - _Shadow2Blur, _Shadow2Border + _Shadow2Blur, light.lightMap);
            vec3 deepShade = _Shadow2Color.rgb * _Shadow2Color.a * secondShade.rgb;
#endif
            vec3 firstShadeColor = _ShadowColor.rgb * _ShadowColor.a * firstShade.rgb;
            vec3 layeredShade = mix(deepShade, firstShadeColor, secondBand);
            finalLight = mix(light.color * layeredShade, light.color, shadow);
            break;
        }
        
        case 2: // Wrapped
        {
            float wrap = _LightingWrappedWrap;
            float wrappedNDotL = light.nDotL + wrap;
            if (_LightingWrappedNormalization > 0.5) {
                wrappedNDotL /= max(1.0 + wrap, EPSILON);
            }
            shadow = smoothstep(_LightingGradientStart, _LightingGradientEnd, saturate(wrappedNDotL));
            
            vec3 shadowColor = _LightingShadowColor;
            finalLight = mix(light.color * shadowColor, light.color, shadow);
            break;
        }

        case 3: // Skin
        {
            float wrap = max(_LightingWrappedWrap, 0.35);
            float skinLight = saturate((light.nDotL + wrap) / (1.0 + wrap));
            shadow = smoothstep(_LightingGradientStart, _LightingGradientEnd, skinLight);
#ifdef XRENGINE_UBER_DISABLE_ADVANCED_STYLIZED_LIGHTING
            vec3 skinShadow = mix(_LightingShadowColor, vec3(1.0, 0.72, 0.58), 0.35);
#else
            vec3 skinShadow = mix(_LightingShadowColor, _LightingSkinColor.rgb, saturate(_LightingSkinScatter));
#endif
            finalLight = mix(light.color * skinShadow, light.color, shadow);
            break;
        }

        case 4: // ShadeMap
        {
            vec2 shadeUV = panUV(transformUV(getUV(_FirstShadeMapUV, mesh), _FirstShadeMap_ST), _FirstShadeMapPan, u_Time);
            vec4 shadeMap = texture(_FirstShadeMap, shadeUV);
            shadow = smoothstep(_ShadowBorder - _ShadowBlur, _ShadowBorder + _ShadowBlur, light.lightMap);
            vec3 shadeColor = mix(_LightingShadowColor, shadeMap.rgb, shadeMap.a);
            finalLight = mix(light.color * shadeColor, light.color, shadow);
            break;
        }
        
        case 5: // Flat
        {
            // Flat mode intentionally ignores NdotL. Each direct light
            // contributes a constant band, with only attenuation / explicit
            // shadowing allowed to reduce it.
            shadow = _ForceFlatRampedLightmap > 0.5 ? step(0.5, light.lightMap) : 1.0;
            finalLight = light.color * shadow;
            break;
        }

        case 6: // Realistic (plain Lambert)
        {
            shadow = saturate(light.nDotL);
            finalLight = light.color * shadow;
            break;
        }

        case 7: // Cloth
        {
            float clothLight = saturate(light.lightMap);
            shadow = smoothstep(_ShadowBorder - _ShadowBlur, _ShadowBorder + _ShadowBlur, clothLight);
#ifdef XRENGINE_UBER_DISABLE_ADVANCED_STYLIZED_LIGHTING
            float grazingFiber = 0.85 + 0.15 * pow(1.0 - saturate(abs(light.nDotV)), 2.0);
#else
            float grazingFiber = 1.0 + _LightingClothSheen * pow(1.0 - saturate(abs(light.nDotV)), mix(8.0, 1.0, saturate(_LightingClothRoughness)));
#endif
            finalLight = mix(light.color * _LightingShadowColor, light.color, shadow) * grazingFiber;
            break;
        }

        case 8: // SDF
        {
#ifdef XRENGINE_UBER_DISABLE_ADVANCED_STYLIZED_LIGHTING
            vec3 localDir = normalize(mesh.localPos + normal * 0.25);
            float sdfLight = saturate(dot(localDir, light.direction) * 0.5 + 0.5);
            shadow = smoothstep(_ShadowBorder - _ShadowBlur, _ShadowBorder + _ShadowBlur, min(light.lightMap, sdfLight));
#else
            vec2 sdfUv = transformUV(getUV(_LightingSDFMapUV, mesh), _LightingSDFMap_ST);
            float sourceSdf = texture(_LightingSDFMap, sdfUv).r;
            vec2 direction = normalize(_LightingSDFDirection);
            float directionBias = dot(normalize(mesh.localPos.xy + vec2(EPSILON)), direction) * 0.5 + 0.5;
            float sdfLight = min(sourceSdf, directionBias);
            shadow = smoothstep(_LightingSDFThreshold - _LightingSDFFeather, _LightingSDFThreshold + _LightingSDFFeather, sdfLight);
#endif
            finalLight = mix(light.color * _LightingShadowColor, light.color, shadow);
            break;
        }
        
        default: // Fallback to flat
        {
            shadow = light.lightMap;
            finalLight = light.color * shadow;
            break;
        }
    }

    float engineShadow = saturate(shadowMapFactor * materialShadowMask);
    float combinedShadow = saturate(shadow * engineShadow);

    // Artist-friendly softening toward fully lit.
    float directVisibility = mix(1.0, engineShadow, _ShadowStrength);
    float tintVisibility = mix(1.0, combinedShadow, _ShadowStrength);

    // Colored tint in shadowed regions (e.g. cool blue shadows).
    vec3 shadowTint = mix(_LightingShadowColor, vec3(1.0), tintVisibility);

    // Compose direct lighting separately from ambient/IBL so AO and texture
    // color remain visible even outside direct-light cones. Texture-ramp mode
    // already incorporated that ambient term; only add it directly for the
    // remaining stylized modes.
    vec3 result;
    if (usesTextureRampComposition) {
        float rampEngineVisibility = mix(1.0, saturate(shadowMapFactor), _ShadowStrength);
        vec3 rampEngineShadowColor = _LightingShadowColor * light.indirectColor;
        result = mix(rampEngineShadowColor, textureRampComposition, rampEngineVisibility);
    } else {
        result = baseColor * finalLight * directVisibility * shadowTint + light.indirectColor;
    }

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

vec3 applySourcePrimaryShading(vec3 baseColor, ToonLight light, ToonMesh mesh,
    vec3 normal, float materialAo)
{
#ifdef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    return baseColor;
#else
    if (_LightingMode == 1)
    {
        float visibility = sampleShadowMap(mesh);
        float map = xrSourceLightMap(dot(normal, light.direction),
            _LightingMapMode, 0.0, 1.0);
        if (_LightingMapMode == 0)
        {
            vec3 rawPrimary = DirLightCount > 0
                ? DirectionalLights[0].Base.Color * DirectionalLights[0].Base.DiffuseIntensity
                : vec3(0.0);
            XrSourceShCoefficients sh = getCanonicalSourceSh();
            vec3 sourceDirection, sourceDirect, sourceIndirect;
            xrSourceShLight(sh, rawPrimary, normal, light.direction,
                _LightingColorMode, _SourceLightSettings.x,
                _LightingIndirectUsesNormals, sourceDirection, sourceDirect,
                sourceIndirect);
            map = xrSourceShLightMap(sh, normal, sourceDirect,
                dot(normal, light.direction), 1.0, 0.0);
        }
        return xrSourceMultilayerDiffuse(baseColor, light.color,
            light.indirectColor, map, vec2(dFdx(map), dFdy(map)), visibility,
            vec2(dFdx(visibility), dFdy(visibility)),
            sampleLightingShadowMask(mesh), true,
            sourceMultilayerInputs(mesh, light.direction));
    }
    if (_LightingMode == 5) {
        float visibility = 1.0;
        if (_SourceLightSettings.y > 0.0) {
            visibility = sampleShadowMap(mesh);
        }
        return baseColor * light.color * mix(1.0, visibility,
            clamp(_SourceLightSettings.y, 0.0, 1.0));
    } else {
        if (_LightingMode == 2)
        {
            vec3 rawPrimary = DirLightCount > 0
                ? DirectionalLights[0].Base.Color * DirectionalLights[0].Base.DiffuseIntensity
                : vec3(0.0);
            XrSourceShCoefficients sh = getCanonicalSourceSh();
            float visibility = sampleShadowMap(mesh);
            float map = xrSourceWrappedMap(normal, light.direction, rawPrimary,
                sh, _LightingWrappedWrap, _LightingWrappedNormalization,
                materialAo, visibility, _SourceLightSettings.y, 1.0,
                _LightingMonochromatic);
            return xrSourceWrappedDiffuse(baseColor, normal, light.direction,
                rawPrimary, sh, _SourceWrappedColor.rgb,
                _LightingShadowColor, _LightingWrappedWrap,
                _LightingWrappedNormalization, materialAo, visibility,
                _SourceLightSettings.y, 1.0, _LightingMonochromatic,
                _ShadowStrength, _LightingIgnoreAmbientColor,
                _LightingGradientStart, _LightingGradientEnd,
                _LightingMinLightBrightness, _LightingCapEnabled, _LightingCap,
                fwidth(map));
        } else {
            float map = xrSourceLightMap(dot(normal, light.direction), _LightingMapMode,
                _SourceLightSettings.y, sampleShadowMap(mesh));
            if (_LightingMapMode == 0) {
                vec3 rawPrimary = DirLightCount > 0
                    ? DirectionalLights[0].Base.Color * DirectionalLights[0].Base.DiffuseIntensity
                    : vec3(0.0);
                XrSourceShCoefficients sh = getCanonicalSourceSh();
                vec3 sourceDirection, sourceDirect, sourceIndirect;
                xrSourceShLight(sh, rawPrimary, normal, light.direction,
                    _LightingColorMode, _SourceLightSettings.x,
                    _LightingIndirectUsesNormals, sourceDirection, sourceDirect,
                    sourceIndirect);
                map = xrSourceShLightMap(sh, normal, sourceDirect,
                    dot(normal, light.direction), sampleShadowMap(mesh),
                    _SourceLightSettings.y);
            }
            vec2 rampCoord = transformUV(vec2(clamp(map + _ShadowOffset, 0.0, 1.0),
                getUV(_ToonRampUV, mesh).y), _ToonRamp_ST);
            rampCoord = panUV(rampCoord, _ToonRampPan, u_Time);
            vec3 ramp = texture(_ToonRamp, rampCoord).rgb;
            return xrSourceRampDiffuse(baseColor, light.color, light.indirectColor,
                ramp, _LightingShadowColor, _ShadowStrength,
                sampleLightingShadowMask(mesh), _LightingIgnoreAmbientColor, materialAo);
        }
    }
#endif
}

vec3 applyShading(vec3 baseColor, ToonLight light, ToonMesh mesh, vec3 normal) {
#ifdef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    return baseColor * (light.color * saturate(light.nDotL) + light.indirectColor);
#else
    return applyShadingWithShadow(baseColor, light, mesh, normal, sampleShadowMap(mesh));
#endif
}

vec3 calculateSourceLocalDiffuse(ToonMesh mesh, vec3 baseColor, vec3 normal,
    vec3 direction, vec3 rawRadiance, float attenuation, float visibility)
{
#ifdef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    return vec3(0.0);
#else
    vec3 direct, indirect;
    xrSourceAdditiveColors(rawRadiance, attenuation, _SourceAdditiveSettings,
        _SourceAdditiveColor, false, direct, indirect);
    if (_LightingMode == 1 && _SourceAdditiveColor.z >= 1.5)
    {
        float map = xrSourceAdditiveLightMap(dot(normal, direction),
            _LightingMapMode, 1.0, 0.0);
        return xrSourceMultilayerDiffuse(baseColor, direct, indirect,
            map, vec2(dFdx(map), dFdy(map)), visibility,
            vec2(dFdx(visibility), dFdy(visibility)),
            sampleLightingShadowMask(mesh),
            false, sourceMultilayerInputs(mesh, direction));
    }
    vec3 ramp = vec3(1.0);
    float shadowMask = 1.0;
    float materialAo = 1.0;
    // Flat additive shading does not consume ramp, mask, or occlusion samples.
    if (_LightingMode != 5) {
        float map = xrSourceAdditiveLightMap(dot(normal, direction), _LightingMapMode,
            visibility, _SourceAdditiveColor.w);
        vec2 rampCoord = transformUV(vec2(clamp(map + _ShadowOffset, 0.0, 1.0),
            getUV(_ToonRampUV, mesh).y), _ToonRamp_ST);
        rampCoord = panUV(rampCoord, _ToonRampPan, u_Time);
        ramp = texture(_ToonRamp, rampCoord).rgb;
        shadowMask = sampleLightingShadowMask(mesh);
        materialAo = sampleMaterialAmbientOcclusion(mesh);
    }
    return xrSourceAdditiveDiffuse(baseColor, direct, indirect, ramp,
        _LightingShadowColor, dot(normal, direction), visibility,
        shadowMask, materialAo,
        _ShadowStrength, _LightingIgnoreAmbientColor, _LightingMode,
        _SourceAdditiveColor, _SourceAdditiveGradient);
#endif
}

// Both responses use the same receiver and light order. Keep one shadow tree.
vec3 calculateUberLocalLighting(ToonMesh mesh, vec3 normal, vec3 baseColor,
    PBRData pbr) {
    vec3 totalLight = vec3(0.0);
#ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    if (_LightingMode != 6 && _SourceLightMode != 0 && _SourceAdditiveSettings.x <= 0.5) {
        return totalLight;
    } else
#endif
    {
        vec3 rms = vec3(0.0);
#ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
        if (_LightingMode == 6)
#endif
        {
            rms = resolveSurfaceRms(pbr);
#ifndef XRENGINE_UBER_DISABLE_ADVANCED_SPECULAR
            if (_SourceMochieSecond.w > 0.5) rms.z = 0.0;
#endif
        }
        int pointCount = max(PointLightCount, 0);
        int lightCount = pointCount + max(SpotLightCount, 0);
        int baseIndex = 0;
        if (ForwardPlusEnabled) {
            baseIndex = getForwardPlusVisibleLightBaseIndex();
            lightCount = ForwardPlusMaxLightsPerTile;
        }

        for (int ordinal = 0; ordinal < lightCount; ++ordinal) {
            UberLocalLight light;
            if (!trySelectUberLocalLight(ordinal, pointCount, baseIndex, light)) {
                break;
            }
            vec3 lightVector = light.position - mesh.worldPos;
            float lightDistance = length(lightVector);
            if (lightDistance <= EPSILON) {
                continue;
            }
            vec3 direction = lightVector / lightDistance;
            float distanceAttenuation = XRENGINE_Attenuate(lightDistance, light.radius);
            float pointAttenuation = distanceAttenuation * light.brightness;
            float shadow = 1.0;
            float spotEffect = 1.0;
            float spotAttn = 1.0;
            // Use canonical records for projection, bias, contact, and filtering.
            if (light.isPoint) {
                if (light.sourceIndex >= 0 && light.sourceIndex < PointLightCount) {
                    shadow = XRENGINE_ReadShadowMapPoint(light.sourceIndex, PointLights[light.sourceIndex],
                        mesh.vertexNormal, mesh.worldPos);
                }
            } else {
                float cosine = max(0.0, dot(-direction, normalize(light.spotDirection)));
                spotEffect = smoothstep(light.outerCutoff, light.innerCutoff, cosine);
                spotAttn = pow(cosine, light.exponent);
                if (light.sourceIndex >= 0 && light.sourceIndex < SpotLightCount) {
                    shadow = XRENGINE_ReadShadowMapSpot(light.sourceIndex, SpotLights[light.sourceIndex],
                        mesh.vertexNormal, mesh.worldPos, direction);
                }
            }
#ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
            if (_LightingMode == 6)
#endif
            {
                vec3 response = XRENGINE_CalculateDirectPbrLightWithViewDir(light.color, light.diffuseIntensity,
                    direction, normal, mesh.worldPos, baseColor, rms, pbr.F0, pointAttenuation, mesh.viewDir);
                totalLight += spotEffect * spotAttn * response * shadow;
            }
#ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
            else {
                // Stylized spot attenuation includes the cone before the response.
                float attenuation = light.isPoint ? pointAttenuation
                    : spotEffect * spotAttn * distanceAttenuation * light.brightness;
                vec3 rawRadiance = light.color * light.diffuseIntensity;
                if (_SourceLightMode != 0) {
                    totalLight += calculateSourceLocalDiffuse(mesh, baseColor, normal,
                        direction, rawRadiance, attenuation, shadow);
                } else {
                    ToonLight toonLight = createToonLight(mesh, normal, direction,
                        rawRadiance, attenuation, vec3(0.0));
                    totalLight += applyShadingWithShadow(baseColor, toonLight, mesh, normal, shadow);
                }
            }
#endif
        }
        return totalLight;
    }
}

vec3 calculateStylizedAdditionalDirectionalLighting(ToonMesh mesh, vec3 baseColor, vec3 normal) {
    vec3 totalLight = vec3(0.0);
    if (_SourceLightMode != 0 && _SourceAdditiveSettings.x <= 0.5) {
        return totalLight;
    } else {
        for (int i = 1; i < DirLightCount; ++i) {
            if (_SourceLightMode != 0 && _SourceAdditiveSettings.y > 0.5) break;
            if (_SourceLightMode != 0) {
                vec3 direction = normalize(-DirectionalLights[i].Direction);
                totalLight += calculateSourceLocalDiffuse(mesh, baseColor, normal,
                    direction, DirectionalLights[i].Base.Color *
                        DirectionalLights[i].Base.DiffuseIntensity, 1.0, 1.0);
            } else {
                ToonLight light = createToonLight(
                    mesh,
                    normal,
                    -DirectionalLights[i].Direction,
                    DirectionalLights[i].Base.Color * DirectionalLights[i].Base.DiffuseIntensity,
                    1.0,
                    vec3(0.0));
                totalLight += applyShadingWithShadow(baseColor, light, mesh, normal, 1.0);
            }
        }
        return totalLight;
    }
}

// ============================================
// Emission
// ============================================
// Self-illumination: texture * color * strength. Does not interact with
// lighting — it's added on top at the end so it survives tonemap/bloom.
vec3 calculateEmission(ToonMesh mesh) {
#ifdef XRENGINE_UBER_DISABLE_EMISSION
    return vec3(0.0);
#else
    vec2 emissionUV = transformUV(getUV(_EmissionMapUV, mesh), _EmissionMap_ST);
    emissionUV = panUV(emissionUV, _EmissionMapPan, u_Time);
    
    // Scrolling emission
    if (_EmissionScrollingEnabled > 0.5) {
        emissionUV += _EmissionScrollingSpeed * u_Time;
        emissionUV += mesh.vertexColor.rg * _EmissionScrollingVertexColor;
    }
    
    vec4 emissionTex = texture(_EmissionMap, emissionUV);
    vec3 emissionColor = uberResolveThemeColor(_EmissionColorThemeIndex, _EmissionColor.rgb);
    vec3 emission = emissionTex.rgb * emissionColor * _EmissionStrength;
    
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
    // Calculate matcap UV based on view-space normal
    vec3 matcapNormal = normalize(mix(mesh.vertexNormal, normal, saturate(_MatcapNormal)));
    vec3 viewNormal = normalize(mat3(u_ViewMatrix) * matcapNormal);
    
    vec2 matcapUV;
    switch(_MatcapUVMode) {
        case 0: // View normal
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
// Fresnel-style edge highlight with three artist-selectable curve shapes.
vec3 calculateRimLight(ToonMesh mesh, vec3 normal, ToonLight light) {
#ifdef XRENGINE_UBER_DISABLE_RIM_LIGHTING
    return vec3(0.0);
#else
    if (_SourceRimSettings.x > 0.5) return vec3(0.0);
    float nDotV = saturate(dot(normal, mesh.viewDir));
    
    float rim;
    switch(_RimStyle) {
        case 0: // Smooth
            rim = 1.0 - nDotV;
            rim = pow(rim, (1.0 - _RimWidth) * 10.0);
            rim = smoothstep(0.0, 1.0 - _RimSharpness, rim);
            break;
        case 1: // Exponential
            rim = pow(1.0 - nDotV, exp2(mix(4.0, 0.0, _RimWidth)));
            break;
        case 2: // Sharp
            rim = saturate((1.0 - nDotV - (1.0 - _RimWidth)) / _RimWidth);
            rim = pow(rim, 1.0 / max(_RimSharpness, EPSILON));
            break;
        default:
            rim = 1.0 - nDotV;
    }
    
    // Sample mask
    vec2 maskUV = transformUV(getUV(_RimMaskUV, mesh), _RimMask_ST);
    float mask = texture(_RimMask, maskUV)[_RimMaskChannel];
    rim *= mask;
    
    // Hide in shadow
    rim *= mix(1.0, light.lightMap, _RimHideInShadow);
    
    // Apply color
    vec2 colorUV = transformUV(getUV(_RimColorTextureUV, mesh), _RimColorTexture_ST);
    colorUV = panUV(colorUV, _RimColorTexturePan, u_Time);
    vec3 rimTextureColor = texture(_RimColorTexture, colorUV).rgb;
    vec3 rimColor = uberResolveThemeColor(_RimLightColorThemeIndex, _RimLightColor.rgb) * rimTextureColor * rim;
    
    // Mix with light color
    rimColor = mix(rimColor, rimColor * light.color, _RimLightColorBias);
    
    return rimColor;
#endif
}

vec3 calculateSourceDirectColor(vec3 radiance) {
#ifdef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    return max(radiance, vec3(0.0));
#else
    return xrSourceDirectColor(radiance, _LightingMinLightBrightness,
        _LightingMonochromatic, _LightingCapEnabled, _LightingCap);
#endif
}

vec3 calculateSourcePrimaryColor(vec3 radiance) {
#if defined(XRENGINE_UBER_DISABLE_STYLIZED_SHADING) || defined(XRENGINE_UBER_DISABLE_FORWARD_LIGHTING)
    return radiance;
#else
    if (_SourceLightMode == 0) return calculateSourceDirectColor(radiance);
    vec3 direct, indirect;
    xrSourceConstantAmbientLight(max(GlobalAmbient, vec3(0.0)), radiance,
        _LightingColorMode, _SourceLightSettings.x, direct, indirect);
    return max(xrSourceDirectColor(direct, _LightingMinLightBrightness,
        _LightingMonochromatic, _LightingCapEnabled, _LightingCap) *
        _SourceLightSettings.z + vec3(_SourceLightSettings.w), vec3(0.0));
#endif
}

vec3 calculateAdvancedSpecular(ToonMesh mesh, vec3 normal, ToonLight light, PBRData pbr, vec3 baseColor) {
#ifdef XRENGINE_UBER_DISABLE_ADVANCED_SPECULAR
    return vec3(0.0);
#else
    if (_SourceMochieSecond.w > 0.5) {
#ifdef XRENGINE_UBER_DISABLE_FORWARD_LIGHTING
        return vec3(0.0);
#else
        vec2 smoothnessUV = panUV(transformUV(getUV(_PBRSmoothnessMapsUV, mesh), _PBRSmoothnessMaps_ST),
            _PBRSmoothnessMapsPan, u_Time);
        vec4 sampled = texture(_PBRSmoothnessMaps, smoothnessUV);
        float packedSmoothness = sampled[clamp(_PBRSmoothnessMapsChannel, 0, 3)];
        if (_PBRSmoothnessMapInvert > 0.5) packedSmoothness = 1.0 - packedSmoothness;
        float materialAo = sampleMaterialAmbientOcclusion(mesh);
        float primarySmoothness = packedSmoothness * _PBRRoughnessMultiplier;
        vec3 normalDx = dFdx(normal), normalDy = dFdy(normal);
        vec3 sourceSpecular = _SourceLightMode != 0
            ? xrSourceMochieDirect(normal, mesh.viewDir, light.direction,
                baseColor, pbr.metallic, primarySmoothness, packedSmoothness,
                pbr.specularMask, _SpecularTint.rgb, light.color, materialAo,
                sampleShadowMap(mesh), normalDx, normalDy,
                _SourceMochieSettings, _SourceMochieSecond, true)
            : vec3(0.0);
        if (_SourceLightMode != 0 && _SourceAdditiveSettings.x <= 0.5)
            return sourceSpecular;
        for (int index = _SourceLightMode != 0 ? 1 : 0; index < DirLightCount; ++index) {
            if (_SourceLightMode != 0 && index > 0 && _SourceAdditiveSettings.y > 0.5) continue;
            DirLight sourceLight = DirectionalLights[index];
            vec3 direction = normalize(-sourceLight.Direction);
            float shadow = index == 0 ? sampleShadowMap(mesh) : 1.0;
            vec3 rawRadiance = sourceLight.Base.Color * sourceLight.Base.DiffuseIntensity;
            vec3 radiance = index == 0
                ? calculateSourcePrimaryColor(rawRadiance)
                : (_SourceLightMode != 0
                    ? xrSourceAdditiveColor(rawRadiance, 1.0, _SourceAdditiveSettings.z,
                        _SourceAdditiveSettings.w, _SourceAdditiveColor.x)
                    : calculateSourceDirectColor(rawRadiance));
            sourceSpecular += xrSourceMochieDirect(normal, mesh.viewDir, direction,
                baseColor, pbr.metallic, primarySmoothness, packedSmoothness,
                pbr.specularMask, _SpecularTint.rgb, radiance, materialAo, shadow,
                normalDx, normalDy, _SourceMochieSettings, _SourceMochieSecond, true);
        }
        if (ForwardPlusEnabled) {
            int baseIndex = getForwardPlusVisibleLightBaseIndex();
            for (int offset = 0; offset < ForwardPlusMaxLightsPerTile; ++offset) {
                int lightIndex = ForwardPlusVisibleIndices[baseIndex + offset];
                if (lightIndex < 0) break;
                ForwardPlusLocalLight sourceLight = ForwardPlusLocalLights[lightIndex];
                vec3 delta = sourceLight.PositionWS.xyz - mesh.worldPos;
                float distance = length(delta);
                if (distance <= EPSILON) continue;
                vec3 direction = delta / distance;
                float attenuation = XRENGINE_Attenuate(distance, sourceLight.Params.x) *
                    max(sourceLight.Params.y, 0.0001);
                float shadow = 1.0;
                int sourceIndex = int(sourceLight.Params.w + 0.5);
                if (sourceLight.Color_Type.w < 0.5) {
                    if (sourceIndex >= 0 && sourceIndex < PointLightCount)
                        shadow = XRENGINE_ReadShadowMapPoint(sourceIndex,
                            PointLights[sourceIndex], mesh.vertexNormal, mesh.worldPos);
                } else {
                    float cosine = max(dot(-direction, normalize(sourceLight.DirectionWS_Exponent.xyz)), 0.0);
                    attenuation *= smoothstep(sourceLight.SpotAngles.y,
                        sourceLight.SpotAngles.x, cosine) * pow(cosine, sourceLight.DirectionWS_Exponent.w);
                    if (sourceIndex >= 0 && sourceIndex < SpotLightCount)
                        shadow = XRENGINE_ReadShadowMapSpot(sourceIndex,
                            SpotLights[sourceIndex], mesh.vertexNormal, mesh.worldPos, direction);
                }
                vec3 rawRadiance = sourceLight.Color_Type.xyz * sourceLight.Params.z;
                vec3 radiance = _SourceLightMode != 0
                    ? xrSourceAdditiveColor(rawRadiance, attenuation, _SourceAdditiveSettings.z,
                        _SourceAdditiveSettings.w, _SourceAdditiveColor.x)
                    : calculateSourceDirectColor(rawRadiance) * attenuation;
                sourceSpecular += xrSourceMochieDirect(normal, mesh.viewDir, direction,
                    baseColor, pbr.metallic, primarySmoothness, packedSmoothness,
                    pbr.specularMask, _SpecularTint.rgb, radiance, materialAo,
                    shadow, normalDx, normalDy, _SourceMochieSettings,
                    _SourceMochieSecond, false);
            }
        } else {
            for (int index = 0; index < PointLightCount; ++index) {
                PointLight sourceLight = PointLights[index];
                vec3 delta = sourceLight.Position - mesh.worldPos;
                float distance = length(delta);
                if (distance <= EPSILON) continue;
                float attenuation = XRENGINE_Attenuate(distance, sourceLight.Radius) * sourceLight.Brightness;
                float shadow = XRENGINE_ReadShadowMapPoint(index, sourceLight,
                    mesh.vertexNormal, mesh.worldPos);
                vec3 rawRadiance = sourceLight.Base.Color * sourceLight.Base.DiffuseIntensity;
                vec3 radiance = _SourceLightMode != 0
                    ? xrSourceAdditiveColor(rawRadiance, attenuation, _SourceAdditiveSettings.z,
                        _SourceAdditiveSettings.w, _SourceAdditiveColor.x)
                    : calculateSourceDirectColor(rawRadiance) * attenuation;
                sourceSpecular += xrSourceMochieDirect(normal, mesh.viewDir, delta / distance,
                    baseColor, pbr.metallic, primarySmoothness, packedSmoothness,
                    pbr.specularMask, _SpecularTint.rgb, radiance, materialAo,
                    shadow, normalDx, normalDy, _SourceMochieSettings,
                    _SourceMochieSecond, false);
            }
            for (int index = 0; index < SpotLightCount; ++index) {
                SpotLight sourceLight = SpotLights[index];
                vec3 delta = sourceLight.Base.Position - mesh.worldPos;
                float distance = length(delta);
                if (distance <= EPSILON) continue;
                vec3 direction = delta / distance;
                float cosine = max(dot(-direction, normalize(sourceLight.Direction)), 0.0);
                float attenuation = XRENGINE_Attenuate(distance, sourceLight.Base.Radius) *
                    sourceLight.Base.Brightness * smoothstep(sourceLight.OuterCutoff,
                    sourceLight.InnerCutoff, cosine) * pow(cosine, sourceLight.Exponent);
                float shadow = XRENGINE_ReadShadowMapSpot(index, sourceLight,
                    mesh.vertexNormal, mesh.worldPos, direction);
                vec3 rawRadiance = sourceLight.Base.Base.Color * sourceLight.Base.Base.DiffuseIntensity;
                vec3 radiance = _SourceLightMode != 0
                    ? xrSourceAdditiveColor(rawRadiance, attenuation, _SourceAdditiveSettings.z,
                        _SourceAdditiveSettings.w, _SourceAdditiveColor.x)
                    : calculateSourceDirectColor(rawRadiance) * attenuation;
                sourceSpecular += xrSourceMochieDirect(normal, mesh.viewDir, direction,
                    baseColor, pbr.metallic, primarySmoothness, packedSmoothness,
                    pbr.specularMask, _SpecularTint.rgb, radiance, materialAo,
                    shadow, normalDx, normalDy, _SourceMochieSettings,
                    _SourceMochieSecond, false);
            }
        }
        return sourceSpecular;
#endif
    }
    if (resolveSpecularIntensity(pbr) <= 0.0) {
        return vec3(0.0);
    }

    vec2 specUV = transformUV(mesh.uv[0], _SpecularMap_ST);
    vec4 specTex = texture(_SpecularMap, specUV);
    float specMask = saturate(max(max(specTex.r, specTex.g), max(specTex.b, specTex.a)));
    vec3 specTint = max(specTex.rgb * _SpecularTint.rgb, vec3(0.0));
    float strength = _SpecularStrength * specMask * pbr.specularMask;
    if (_PBRMochiePackedMasksEnabled != 0)
        strength = pbr.specularMask;
    strength *= mix(0.5, 2.0, saturate(_StylizedSpecular));

#ifdef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    if (_SpecularType == 0) {
        return vec3(0.0);
    }
#else
    if (_SpecularType == 0 && _LightingMode == 6) {
        return vec3(0.0);
    }
#endif

    if (_SpecularType == 0) {
        float exponent = mix(8.0, 256.0, saturate(_SpecularSmoothness));
        float highlight = pow(saturate(light.nDotH), exponent) * saturate(light.nDotL);
        return specTint * light.color * highlight * strength;
    }

    if (_SpecularType == 1) {
        float toonSize = mix(0.35, 0.98, saturate(_SpecularSmoothness));
        float toonFeather = mix(0.08, 0.005, saturate(_SpecularSmoothness));
        float highlight = smoothstep(toonSize - toonFeather, toonSize + toonFeather, saturate(light.nDotH));
        return specTint * light.color * highlight * strength * saturate(light.nDotL);
    }

    vec3 tangent = normalize(mesh.TBN[0]);
    float tDotH = dot(tangent, light.halfDir);
    float anisoBand = sqrt(max(0.0, 1.0 - tDotH * tDotH));
    float exponent = mix(8.0, 128.0, saturate(_SpecularSmoothness));
    float highlight = pow(anisoBand, exponent) * saturate(light.nDotL);
    return specTint * light.color * highlight * strength;
#endif
}

// ============================================
// Main Fragment Function
// ============================================
vec3 calculateSourceClearCoat(ToonMesh mesh, vec3 normal, ToonLight primaryLight)
{
#if defined(XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS) || defined(XRENGINE_UBER_DISABLE_FORWARD_LIGHTING) || defined(XRENGINE_UBER_DISABLE_ADVANCED_PBR)
    return vec3(0.0);
#else
    if (_SourceClearCoat.x <= 0.5) return vec3(0.0);
    vec4 packed = texture(_SourceClearCoatMaps,
        transformUV(mesh.uv[0], _SourceClearCoatMaps_ST));
    float smoothness = _SourceClearCoat.y * packed.g;
    float specularMask = _SourceClearCoat.w * packed.a;
    float materialAo = sampleMaterialAmbientOcclusion(mesh);
    vec3 normalDx = dFdx(normal), normalDy = dFdy(normal);
    vec3 direct = xrSourceGgxLobe(normal, mesh.viewDir, primaryLight.direction,
        vec3(0.220916301), smoothness, specularMask, vec3(1.0),
        primaryLight.color, materialAo, sampleShadowMap(mesh), normalDx,
        normalDy, _SourceClearCoatGsaa, 0.0, true);
    if (_SourceLightMode == 0 || _SourceAdditiveSettings.x > 0.5)
    {
        for (int index = 1; index < DirLightCount; ++index)
        {
            if (_SourceLightMode != 0 && _SourceAdditiveSettings.y > 0.5) break;
            DirLight source = DirectionalLights[index];
            vec3 raw = source.Base.Color * source.Base.DiffuseIntensity;
            vec3 radiance = _SourceLightMode != 0
                ? xrSourceAdditiveColor(raw, 1.0, _SourceAdditiveSettings.z,
                    _SourceAdditiveSettings.w, _SourceAdditiveColor.x)
                : raw;
            direct += xrSourceGgxLobe(normal, mesh.viewDir, normalize(-source.Direction),
                vec3(0.220916301), smoothness, specularMask, vec3(1.0),
                radiance, materialAo, 1.0, normalDx, normalDy,
                _SourceClearCoatGsaa, 0.0, true);
        }
        if (ForwardPlusEnabled)
        {
            int baseIndex = getForwardPlusVisibleLightBaseIndex();
            for (int offset = 0; offset < ForwardPlusMaxLightsPerTile; ++offset)
            {
                int lightIndex = ForwardPlusVisibleIndices[baseIndex + offset];
                if (lightIndex < 0) break;
                ForwardPlusLocalLight source = ForwardPlusLocalLights[lightIndex];
                vec3 delta = source.PositionWS.xyz - mesh.worldPos;
                float distance = length(delta);
                if (distance <= EPSILON) continue;
                vec3 direction = delta / distance;
                float attenuation = XRENGINE_Attenuate(distance, source.Params.x) *
                    max(source.Params.y, 0.0001);
                if (source.Color_Type.w > 0.5)
                {
                    float cosine = max(dot(-direction,
                        normalize(source.DirectionWS_Exponent.xyz)), 0.0);
                    attenuation *= smoothstep(source.SpotAngles.y, source.SpotAngles.x,
                        cosine) * pow(cosine, source.DirectionWS_Exponent.w);
                }
                vec3 raw = source.Color_Type.xyz * source.Params.z;
                vec3 radiance = _SourceLightMode != 0
                    ? xrSourceAdditiveColor(raw, attenuation, _SourceAdditiveSettings.z,
                        _SourceAdditiveSettings.w, _SourceAdditiveColor.x)
                    : raw * attenuation;
                direct += xrSourceGgxLobe(normal, mesh.viewDir, direction,
                    vec3(0.220916301), smoothness, specularMask, vec3(1.0),
                    radiance, materialAo,
                    _SourceLightMode != 0 ? attenuation : 1.0,
                    normalDx, normalDy, _SourceClearCoatGsaa, 0.0,
                    _SourceLightMode != 0);
            }
        }
        else
        {
            for (int index = 0; index < PointLightCount; ++index)
            {
                PointLight source = PointLights[index];
                vec3 delta = source.Position - mesh.worldPos;
                float distance = length(delta);
                if (distance <= EPSILON) continue;
                float attenuation = XRENGINE_Attenuate(distance, source.Radius) *
                    source.Brightness;
                vec3 raw = source.Base.Color * source.Base.DiffuseIntensity;
                vec3 radiance = _SourceLightMode != 0
                    ? xrSourceAdditiveColor(raw, attenuation, _SourceAdditiveSettings.z,
                        _SourceAdditiveSettings.w, _SourceAdditiveColor.x)
                    : raw * attenuation;
                direct += xrSourceGgxLobe(normal, mesh.viewDir, delta / distance,
                    vec3(0.220916301), smoothness, specularMask, vec3(1.0),
                    radiance, materialAo,
                    _SourceLightMode != 0 ? attenuation : 1.0,
                    normalDx, normalDy, _SourceClearCoatGsaa, 0.0,
                    _SourceLightMode != 0);
            }
            for (int index = 0; index < SpotLightCount; ++index)
            {
                SpotLight source = SpotLights[index];
                vec3 delta = source.Base.Position - mesh.worldPos;
                float distance = length(delta);
                if (distance <= EPSILON) continue;
                vec3 direction = delta / distance;
                float cosine = max(dot(-direction, normalize(source.Direction)), 0.0);
                float attenuation = XRENGINE_Attenuate(distance, source.Base.Radius) *
                    source.Base.Brightness * smoothstep(source.OuterCutoff,
                    source.InnerCutoff, cosine) * pow(cosine, source.Exponent);
                vec3 raw = source.Base.Base.Color * source.Base.Base.DiffuseIntensity;
                vec3 radiance = _SourceLightMode != 0
                    ? xrSourceAdditiveColor(raw, attenuation, _SourceAdditiveSettings.z,
                        _SourceAdditiveSettings.w, _SourceAdditiveColor.x)
                    : raw * attenuation;
                direct += xrSourceGgxLobe(normal, mesh.viewDir, direction,
                    vec3(0.220916301), smoothness, specularMask, vec3(1.0),
                    radiance, materialAo,
                    _SourceLightMode != 0 ? attenuation : 1.0,
                    normalDx, normalDy, _SourceClearCoatGsaa, 0.0,
                    _SourceLightMode != 0);
            }
        }
    }
    vec3 rawReflection = XRENGINE_SampleForwardRawPrefilter(normal,
        mesh.worldPos, mesh.viewDir,
        xrSourcePerceptualRoughness(smoothness, normalDx, normalDy,
            _SourceClearCoatGsaa));
    vec3 reflection = xrSourceClearCoatReflection(rawReflection,
        primaryLight.color, smoothness, _SourceClearCoat.z * packed.b,
        materialAo, abs(dot(normal, mesh.viewDir)), normalDx, normalDy,
        _SourceClearCoatGsaa, _SourceClearCoatReflection);
    return clamp((direct + reflection) * packed.r * _SourceClearCoatGsaa.w,
        vec3(0.0), vec3(1.0));
#endif
}

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
    mesh.uv[0] = FragUV0;
    mesh.uv[1] = FragUV1;
    mesh.uv[2] = FragUV2;
    mesh.uv[3] = FragUV3;
    mesh.worldPos = FragPos;
    mesh.localPos = FragPosLocal;
#if !defined(XRENGINE_UBER_DISABLE_FORWARD_LIGHTING) && !defined(XRENGINE_DEPTH_NORMAL_PREPASS) && !defined(XRENGINE_SHADOW_CASTER_PASS) && !defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
    XRENGINE_BeginForwardLightingFragment(mesh.worldPos);
#endif
    mesh.vertexNormal = normalize(FragNorm);
    mesh.vertexColor = FragColor0;
    mesh.viewDir = normalize(u_CameraPosition - FragPos);
    mesh.isFrontFace = gl_FrontFacing ? 1.0 : -1.0;

    float normalFacing = _SourceBackfaceNormals == 0 || _FlipBackfaceNormals != 0
        ? mesh.isFrontFace : 1.0;
    float frameFacing = _SourceBackfaceNormals != 0 && _FlipBackfaceNormals != 0
        ? mesh.isFrontFace : 1.0;
    mesh.vertexNormal *= normalFacing;
    mesh.worldNormal = mesh.vertexNormal;

    // Prefer the mesh's imported tangent frame. Derivative TBN is only a
    // fallback for meshes that did not provide usable tangents.
    // Initialize to an identity-like frame so downstream helpers that touch
    // TBN directly (rare) still see a well-formed matrix.
#if !defined(XRENGINE_UBER_DISABLE_NORMAL_MAP) || !defined(XRENGINE_UBER_DISABLE_DETAIL_TEXTURES) || !defined(XRENGINE_UBER_DISABLE_PARALLAX)
    {
        mesh.TBN = computeImportedOrFallbackWorldTbn(mesh.vertexNormal,
            FragTan * frameFacing, FragBinorm * frameFacing, mesh.worldPos, mesh.uv[0]);
    }
#else
    {
        vec3 n = mesh.vertexNormal;
        vec3 t = normalize(abs(n.z) < 0.999 ? cross(n, vec3(0.0, 0.0, 1.0)) : cross(n, vec3(0.0, 1.0, 0.0)));
        mesh.TBN = mat3(t, cross(n, t), n);
    }
#endif

    // ---- 2. Depth-peel gate -------------------------------------------------
#if !defined(XRENGINE_DEPTH_NORMAL_PREPASS) && !defined(XRENGINE_SHADOW_CASTER_PASS) && !defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
    XRENGINE_BeginForwardFragmentOutput();
#endif

    if (uberApplyCoverageEffects(mesh))
        discard;
    uberApplyInternalParallax(mesh);

    // ---- 3. Parallax --------------------------------------------------------
    // Parallax occlusion mapping warps UVs along the view ray through a height
    // map to simulate depth. SPOM (silhouette-preserving) additionally
    // discards fragments whose ray steps off the base UV rectangle so the
    // displaced surface also looks correct at grazing angles.
#ifndef XRENGINE_UBER_DISABLE_PARALLAX
    {
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

    // Normal map sampling uses the (possibly parallax-warped) UV0. Keep
    // depth/normal and shadow pre-passes on geometric normals; bump/detail
    // normals describe shading detail, not screen-space occluder shape.
#if (!defined(XRENGINE_UBER_DISABLE_NORMAL_MAP) || !defined(XRENGINE_UBER_DISABLE_DETAIL_TEXTURES)) && !defined(XRENGINE_DEPTH_NORMAL_PREPASS) && !defined(XRENGINE_SHADOW_CASTER_PASS) && !defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
    vec3 tangentNormal = calculateTangentNormal(mesh);
    tangentNormal = applyDetailTangentNormal(mesh, tangentNormal);
    mesh.worldNormal = normalize(mesh.TBN * tangentNormal);
#endif
#if !defined(XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS) && !defined(XRENGINE_DEPTH_NORMAL_PREPASS) && !defined(XRENGINE_SHADOW_CASTER_PASS) && !defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
    mesh.worldNormal = uberApplyColorMaskNormal(uberCorrectNormal(mesh.worldNormal, mesh), mesh);
    if (mesh.isFrontFace < 0.0 && _BackFaceNormalStrength > EPSILON) {
        vec2 backNormalUv = transformUV(getUV(_BackFaceTextureUV, mesh), _BackFaceNormalMap_ST);
        vec3 backNormal = unpackNormal(texture(_BackFaceNormalMap, backNormalUv), _BackFaceNormalStrength);
        mesh.worldNormal = normalize(mix(mesh.worldNormal, mesh.TBN * backNormal, saturate(_BackFaceNormalStrength)));
    }
#endif

    // ---- 4. Base color, color adjustments, alpha ----------------------------
    FragmentData fragData;
    fragData.emission = vec3(0.0);

    vec4 baseColor = calculateBaseColor(mesh);
    baseColor.rgb = applyColorAdjustments(baseColor.rgb, mesh);
    fragData.alpha = calculateAlpha(baseColor, mesh);

    fragData.baseColor = baseColor.rgb;
    uberApplyDecals(fragData, mesh);
    uberApplySurfaceEffects(mesh, fragData.baseColor, fragData.emission, fragData.alpha);

    // ---- 5. Dissolve + early prepass/shadow outputs -------------------------
    // Dissolve may rewrite baseColor/emission/alpha and can discard entirely
    // for fully-dissolved fragments.
#ifndef XRENGINE_UBER_DISABLE_DISSOLVE
    if (applyDissolve(
        getUV(_DissolveNoiseTextureUV, mesh),
        getUV(_DissolveDetailNoiseUV, mesh),
        getUV(_DissolveMaskUV, mesh),
        getUV(_DissolveEdgeTextureUV, mesh),
        mesh.worldPos,
        mesh.localPos,
        fragData.baseColor,
        fragData.emission,
        fragData.alpha)) {
        discard;
    }
#endif

    // Test final alpha after decals and dissolve in every coverage pass.
    if (_Mode == BLEND_MODE_CUTOUT && fragData.alpha < _Cutoff)
        discard;

#if defined(XRENGINE_OUTLINE_PASS)
    // Inverse-hull color output. This branch deliberately lives after the
    // canonical alpha and dissolve coverage path, so the outline silhouette
    // cannot drift from base/depth/shadow coverage.
    vec2 outlineUv = panUV(transformUV(getUV(_OutlineTextureUV, mesh), _OutlineTexture_ST), _OutlineTexturePan, u_Time);
    vec2 outlineMaskUv = panUV(transformUV(getUV(_OutlineMaskUV, mesh), _OutlineMask_ST), _OutlineMaskPan, u_Time);
    vec4 outlineTexture = texture(_OutlineTexture, outlineUv);
    float outlineMask = texture(_OutlineMask, outlineMaskUv).r;
    vec4 outlineColor = _OutlineColor;
    outlineColor.rgb = hueShift(outlineColor.rgb * outlineTexture.rgb, _OutlineHueShift + _OutlineHueShiftSpeed * u_Time);
    outlineColor.rgb = mix(
        outlineColor.rgb,
        outlineColor.rgb * fragData.baseColor,
        saturate(_OutlineTextureTint));
    outlineColor.rgb = mix(
        outlineColor.rgb,
        outlineColor.rgb * mesh.vertexColor.rgb,
        saturate(_OutlineVertexColorTint));
    float rim = pow(1.0 - saturate(dot(mesh.worldNormal, mesh.viewDir)), 2.0);
    outlineColor.rgb *= mix(vec3(1.0), vec3(mix(1.0 - _OutlineShadowStrength, 1.0, rim)), saturate(_OutlineLit));
    outlineColor.rgb += outlineColor.rgb * _OutlineEmission;
    float fadeStart = min(_OutlineDistanceFadeStart, _OutlineDistanceFadeEnd);
    float fadeEnd = max(_OutlineDistanceFadeStart, _OutlineDistanceFadeEnd);
    float distanceFade = 1.0;
    if (fadeEnd > fadeStart)
        distanceFade = 1.0 - smoothstep(fadeStart, fadeEnd,
            length(mesh.worldPos - u_CameraPosition));
    outlineColor.a *= fragData.alpha * outlineTexture.a * outlineMask * distanceFade;
    if (outlineColor.a <= 0.0001)
        discard;

    XRENGINE_WriteForwardFragment(outlineColor);
    return;
#endif

    // Cheap outputs for non-color passes. Done *after* cutout/dissolve so
    // shadow maps and the depth/normal prepass respect those discards.
#if defined(XRENGINE_POINT_SHADOW_CASTER_PASS)
    XRENGINE_WritePointShadowCasterDepth(Depth, FragPos, LightPos, FarPlaneDist);
    return;
#elif defined(XRENGINE_SHADOW_CASTER_PASS)
    XRENGINE_WriteShadowCasterDepth(Depth, gl_FragCoord.z);
    return;
#elif defined(XRENGINE_DEPTH_NORMAL_PREPASS)
    Normal = XRENGINE_EncodeNormal(mesh.worldNormal);
    TransformId = FragRenderIdentityId;
    return;
#else

    // ---- 6. Detail textures + lighting --------------------------------------
    // Detail textures run before lighting so shading responds to the detail-
    // modulated albedo rather than tinting an already-lit result.
#ifndef XRENGINE_UBER_DISABLE_DETAIL_TEXTURES
    {
        vec2 detailUV = transformUV(getUV(_DetailTexUV, mesh), _DetailTex_ST);
        detailUV = panUV(detailUV, _DetailTexPan, u_Time);
        vec3 detailColor = texture(_DetailTex, detailUV).rgb * _DetailTint + _DetailBrightness;

        vec2 detailMaskUV = transformUV(getUV(_DetailMaskUV, mesh), _DetailMask_ST);
        detailMaskUV = panUV(detailMaskUV, _DetailMaskPan, u_Time);
        float detailMask = texture(_DetailMask, detailMaskUV).r;
        float detailStrength = saturate(_DetailTexIntensity * detailMask);

        fragData.baseColor = mix(fragData.baseColor, uberBlendRgb(fragData.baseColor, detailColor, _DetailTexBlendMode), detailStrength);
    }
#endif
    
    // PBR and stylized feature overlays share this surface/light context even
    // when a pass disables direct forward lighting. Keep it defined for those
    // unlit variants and initialize a neutral light below.
    vec3 matcapLightAdd = vec3(0.0);
#ifndef XRENGINE_UBER_DISABLE_LAYERED_MATCAP_RIM
    if (_SourceMatcap > 0.5)
    {
        ToonLight matcapLight = calculateLighting(mesh, mesh.worldNormal, vec3(0.0));
#ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
        if (_SourceLightMode != 0 && _LightingMode == 0)
        {
            vec2 rampUv = vec2(clamp(matcapLight.lightMap + _ShadowOffset, 0.0, 1.0),
                getUV(_ToonRampUV, mesh).y);
            rampUv = rampUv * _ToonRamp_ST.xy + _ToonRamp_ST.zw + _ToonRampPan * u_Time;
            matcapLight.lightMap = mix(1.0, texture(_ToonRamp, rampUv).r,
                clamp(_ShadowStrength * sampleLightingShadowMask(mesh), 0.0, 1.0));
        }
#endif
        uberApplyMatcapSlots(fragData.baseColor, fragData.emission, matcapLightAdd, mesh, matcapLight);
    }
#endif
#ifndef XRENGINE_UBER_DISABLE_ADVANCED_PBR
    if (_SourceCubeMap != 0) {
        ToonLight cubemapLight;
        cubemapLight.lightMap = 1.0;
        if (_CubeMapLightMask > 0.0)
            cubemapLight = calculateLighting(mesh, mesh.worldNormal, vec3(0.0));
#ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
        if (_SourceLightMode != 0 && _LightingMode == 0 && _CubeMapLightMask > 0.0) {
            vec2 rampUv = vec2(clamp(cubemapLight.lightMap + _ShadowOffset, 0.0, 1.0),
                getUV(_ToonRampUV, mesh).y);
            rampUv = rampUv * _ToonRamp_ST.xy + _ToonRamp_ST.zw + _ToonRampPan * u_Time;
            cubemapLight.lightMap = mix(1.0, texture(_ToonRamp, rampUv).r,
                clamp(_ShadowStrength * sampleLightingShadowMask(mesh), 0.0, 1.0));
        }
#endif
        uberApplyCubemap(mesh, cubemapLight, true, fragData.baseColor, fragData.emission);
    }
#endif
#ifndef XRENGINE_UBER_DISABLE_RIM_LIGHTING
    if (_SourceRimSettings.x > 0.5 && _RimStyle == 0) {
        vec2 rimUv = panUV(transformUV(getUV(_RimColorTextureUV, mesh), _RimColorTexture_ST),
            _RimColorTexturePan, u_Time);
        vec4 rimTexel = texture(_RimColorTexture, rimUv);
        vec2 maskUv = transformUV(getUV(_RimMaskUV, mesh), _RimMask_ST);
        float mask = texture(_RimMask, maskUv)[clamp(_RimMaskChannel, 0, 3)];
        float rim = xrSourceRimAmount(mesh.vertexNormal, mesh.worldNormal, mesh.viewDir,
            _RimWidth, _RimSharpness, _SourceRimSettings) * _RimLightColor.a *
            _RimLightColor.a * rimTexel.a * mask;
        vec3 rimColor = rimTexel.rgb * uberResolveThemeColor(_RimLightColorThemeIndex,
            _RimLightColor.rgb) * mix(vec3(1.0), fragData.baseColor,
            _SourceRimColorSettings.x) * _SourceRimColorSettings.y;
        fragData.baseColor = xrSourceRimBlend(fragData.baseColor, rimColor,
            rim * _RimBlendStrength, int(_SourceRimColorSettings.z + 0.5));
        fragData.emission += rimColor * rim * _RimEmission;
    }
#endif
    // Source hair anisotropy changes base color before diffuse shading and
    // adds its strand highlights after the source BRDF.
#if !defined(XRENGINE_UBER_DISABLE_ADVANCED_PBR) && !defined(XRENGINE_UBER_DISABLE_FORWARD_LIGHTING)
    if (_SourceAnisoSettings.x > 0.5) {
        ToonLight anisoLight = calculateLighting(mesh, mesh.worldNormal, vec3(0.0));
        xrSourceApplyAnisotropy(mesh.vertexNormal, mesh.worldNormal,
            mesh.TBN[0], mesh.TBN[1], mesh.viewDir, anisoLight.direction,
            anisoLight.color, anisoLight.lightMap, _SourceAnisoSettings,
            _SourceAnisoMix, _SourceAnisoLayer0, _SourceAnisoLayer1,
            uberResolveThemeColor(int(_SourceAnisoTint0.w + 0.5),
                _SourceAnisoTint0.rgb),
            uberResolveThemeColor(int(_SourceAnisoTint1.w + 0.5),
                _SourceAnisoTint1.rgb), fragData.baseColor, matcapLightAdd);
    }
#endif
    PBRData surfacePbr = buildSurfacePbrData(mesh, fragData.baseColor);
    fragData.baseColor = uberApplyColorMask(fragData.baseColor, mesh, fragData.emission, surfacePbr);
    surfacePbr.F0 = mix(vec3(0.04), fragData.baseColor, surfacePbr.metallic);
    surfacePbr.diffuseColor = fragData.baseColor * (1.0 - surfacePbr.metallic);
    // Occlusion = screen-space SSAO * material-baked AO.
    vec3 sourceProbeDiffuse = vec3(0.0), sourceProbeSpecular = vec3(0.0);
#ifndef XRENGINE_UBER_DISABLE_FORWARD_LIGHTING
#ifndef XRENGINE_UBER_DISABLE_FORWARD_AMBIENT_OCCLUSION
    float screenAmbientOcclusion = XRENGINE_SampleAmbientOcclusion();
#else
    float screenAmbientOcclusion = 1.0;
#endif
#ifndef XRENGINE_UBER_DISABLE_MATERIAL_AO
    float materialAmbientOcclusion = sampleMaterialAmbientOcclusion(mesh);
#else
    float materialAmbientOcclusion = 1.0;
#endif
    float combinedAmbientOcclusion = saturate(screenAmbientOcclusion * materialAmbientOcclusion);
    vec3 ambientLighting = calculateForwardAmbientLightingWithProbe(mesh,
        fragData.baseColor, mesh.worldNormal, surfacePbr,
        combinedAmbientOcclusion, sourceProbeDiffuse, sourceProbeSpecular);
    ToonLight light = calculateLighting(mesh, mesh.worldNormal, ambientLighting);
#else
    ToonLight light = createToonLight(
        mesh,
        mesh.worldNormal,
        vec3(0.0, 1.0, 0.0),
        vec3(0.0),
        1.0,
        vec3(0.0));
#endif

    // ---- 7. Feature overlays (back face, SSS, matcap, rim, etc.) ----------
    // Separate material for the back side of double-sided geometry.
#ifndef XRENGINE_UBER_DISABLE_BACKFACE
    if (mesh.isFrontFace < 0.0) {
        vec2 backUv = panUV(transformUV(getUV(_BackFaceTextureUV, mesh), _BackFaceTexture_ST), _BackFaceTexturePan, u_Time);
        vec4 backTex = texture(_BackFaceTexture, backUv);
        vec3 backLayer = backTex.rgb * _BackFaceColor.rgb;
        int backBlendMode = int(_BackFaceBlendMode + 0.5);
        vec3 backColor = backBlendMode == 1
            ? _BackFaceColor.rgb + backLayer
            : (backBlendMode == 2 ? _BackFaceColor.rgb * backLayer : backLayer);
        fragData.baseColor = mix(fragData.baseColor, backColor, _BackFaceColor.a);
        fragData.alpha = clamp(fragData.alpha * _BackFaceAlpha, 0.0, 1.0);
        fragData.emission += backColor * _BackFaceEmission;
    }
#endif
    
#ifndef XRENGINE_UBER_DISABLE_GLITTER
    if (_SourceGlitter != 0) {
        vec2 uv = getUV(_GlitterUV, mesh);
        vec2 maskUv = getUV(_GlitterMaskUV, mesh) * _GlitterMask_ST.xy +
            _GlitterMask_ST.zw + _GlitterMaskPan * u_Time * 0.05;
        float mask = texture(_GlitterMask, maskUv)[clamp(_GlitterMaskChannel, 0, 3)];
        if (_GlitterMaskInvert > 0.5) mask = 1.0 - mask;
        float ramp = saturate(light.nDotL);
#ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
        ramp = smoothstep(_ShadowBorder - _ShadowBlur, _ShadowBorder + _ShadowBlur, light.lightMap);
        if (_LightingMode == 0) {
            vec2 rampUv = vec2(saturate(light.lightMap + _ShadowOffset), getUV(_ToonRampUV, mesh).y);
            rampUv = rampUv * _ToonRamp_ST.xy + _ToonRamp_ST.zw + _ToonRampPan * u_Time;
            ramp = texture(_ToonRamp, rampUv).r;
        }
        ramp = mix(1.0, ramp, saturate(_ShadowStrength * sampleLightingShadowMask(mesh)));
#endif
        if (_SourceGlitterVersion == 81) {
            float globalMask = 1.0;
#ifndef XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS
            vec2 globalUv = getUV(_GlobalMaskTexture0UV, mesh) * _GlobalMaskTexture0_ST.xy +
                _GlobalMaskTexture0_ST.zw;
            globalMask = texture(_GlobalMaskTexture0, globalUv).g;
#endif
            xrApplySourceGlitter81(uv, dFdx(uv), dFdy(uv), u_Time,
                mesh.vertexNormal, mesh.viewDir, _GlitterColor, mask * globalMask,
                fragData.emission);
        } else {
            xrApplySourceGlitter(uv, dFdx(uv), dFdy(uv),
                dFdx(mesh.uv[0]), dFdy(mesh.uv[0]), u_Time, mesh.vertexNormal, mesh.worldNormal,
                mesh.viewDir, light.direction, light.color, ramp, luminance(light.color),
                _GlitterColor, mask, fragData.baseColor, fragData.emission);
        }
        if (_GlitterBlendType == 1) {
            surfacePbr.F0 = mix(vec3(0.04), fragData.baseColor, surfacePbr.metallic);
            surfacePbr.diffuseColor = fragData.baseColor * (1.0 - surfacePbr.metallic);
#ifndef XRENGINE_UBER_DISABLE_FORWARD_LIGHTING
            ambientLighting = calculateForwardAmbientLightingWithProbe(mesh,
                fragData.baseColor, mesh.worldNormal, surfacePbr,
                combinedAmbientOcclusion, sourceProbeDiffuse, sourceProbeSpecular);
            light = calculateLighting(mesh, mesh.worldNormal, ambientLighting);
#endif
        }
    }
#endif

    vec3 sourceDiffuseBase = fragData.baseColor;
#ifndef XRENGINE_UBER_DISABLE_ADVANCED_SPECULAR
    if (_SourceMochieSecond.w > 0.5)
        sourceDiffuseBase *= mix(1.0, 0.96 * (1.0 - surfacePbr.metallic),
            surfacePbr.reflectionMask);
#endif

#ifndef XRENGINE_UBER_DISABLE_FORWARD_LIGHTING
#ifndef XRENGINE_UBER_DISABLE_ADVANCED_SPECULAR
    if (_SourceMochieSecond.w > 0.5) {
        vec2 smoothnessUv = panUV(transformUV(
            getUV(_PBRSmoothnessMapsUV, mesh), _PBRSmoothnessMaps_ST),
            _PBRSmoothnessMapsPan, u_Time);
        vec4 packedMochie = texture(_PBRSmoothnessMaps, smoothnessUv);
        float packedSmoothness = packedMochie[clamp(_PBRSmoothnessMapsChannel, 0, 3)];
        if (_PBRSmoothnessMapInvert > 0.5)
            packedSmoothness = 1.0 - packedSmoothness;
        float sourceSmoothness = packedSmoothness * _PBRRoughnessMultiplier;
        vec3 normalDx = dFdx(mesh.worldNormal);
        vec3 normalDy = dFdy(mesh.worldNormal);
        vec3 rawReflection = XRENGINE_SampleForwardRawPrefilter(
            mesh.worldNormal, mesh.worldPos, mesh.viewDir,
            xrSourcePerceptualRoughness(sourceSmoothness, normalDx, normalDy,
                _SourceMochieSettings));
        vec3 previousProbeSpecular = sourceProbeSpecular;
        sourceProbeSpecular = xrSourceMochieReflection(rawReflection,
            light.color, mix(vec3(0.04), fragData.baseColor,
                surfacePbr.metallic), sourceSmoothness,
            surfacePbr.reflectionMask, materialAmbientOcclusion,
            abs(dot(mesh.worldNormal, mesh.viewDir)), normalDx, normalDy,
            _SourceMochieSettings, _SourceMochieReflection,
            _SourceMochieReflectionTint.rgb);
        ambientLighting += sourceProbeSpecular - previousProbeSpecular;
        light = calculateLighting(mesh, mesh.worldNormal, ambientLighting);
    }
#endif
#endif

    // Evaluate local shadows once. Keep each directional response separate.
#ifdef XRENGINE_UBER_DISABLE_FORWARD_LIGHTING
    fragData.finalColor = fragData.baseColor;
#else
    vec3 primaryStylizedLighting = vec3(0.0);
    vec3 directionalLighting;
    vec3 localBaseColor = fragData.baseColor;
#ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    if (_LightingMode != 6) {
        localBaseColor = sourceDiffuseBase;
    }
#endif
    vec3 localLighting = calculateUberLocalLighting(mesh, mesh.worldNormal,
        localBaseColor, surfacePbr);
#ifdef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    directionalLighting = calculateForwardDirectionalLighting(mesh, mesh.worldNormal,
        fragData.baseColor, surfacePbr, false);
#else
    // Direct, braced dispatch lets static variants remove unused directionals.
    if (_LightingMode != 6) {
        if (_SourceLightMode != 0 &&
            (_LightingMode == 0 || _LightingMode == 2 || _LightingMode == 5)) {
            primaryStylizedLighting = applySourcePrimaryShading(sourceDiffuseBase, light, mesh,
                mesh.worldNormal, sampleMaterialAmbientOcclusion(mesh));
        } else {
            primaryStylizedLighting = applyShading(sourceDiffuseBase, light, mesh, mesh.worldNormal);
        }
        directionalLighting = calculateStylizedAdditionalDirectionalLighting(mesh,
            sourceDiffuseBase, mesh.worldNormal);
    } else {
        directionalLighting = calculateForwardDirectionalLighting(mesh, mesh.worldNormal,
            fragData.baseColor, surfacePbr, false);
    }
#endif
#ifdef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    fragData.finalColor = (directionalLighting + localLighting) + ambientLighting;
#else
    if (_LightingMode != 6) {
        fragData.finalColor = primaryStylizedLighting + (directionalLighting + localLighting);
    } else {
        fragData.finalColor = (directionalLighting + localLighting) + ambientLighting;
    }
#endif
#endif

#if !defined(XRENGINE_UBER_DISABLE_STYLIZED_SHADING) && !defined(XRENGINE_UBER_DISABLE_FORWARD_LIGHTING)
    if (_SourceLightMode != 0 && _LightingMode != 6)
        fragData.finalColor += sourceProbeSpecular + (SourceSHEnabled ? vec3(0.0) : sourceProbeDiffuse);
#endif

#ifndef XRENGINE_UBER_DISABLE_ADVANCED_SPECULAR
    fragData.finalColor += calculateAdvancedSpecular(mesh, mesh.worldNormal, light, surfacePbr, fragData.baseColor);
#endif
    fragData.finalColor = uberApplyAdvancedPbr(mesh, light, surfacePbr, fragData.finalColor, fragData.emission);
#if !defined(XRENGINE_UBER_DISABLE_ADVANCED_PBR) && !defined(XRENGINE_UBER_DISABLE_FORWARD_LIGHTING)
    if (_SourceEnviroRim.x > 0.5 && _RimEnviroIntensity > 0.0)
        fragData.finalColor += uberApplySourceEnviroRim(mesh, fragData.baseColor,
            XRENGINE_SampleForwardRawPrefilterNormal(mesh.worldNormal,
                mesh.worldPos, _SourceEnviroRim.y));
#endif
    fragData.finalColor += calculateSourceClearCoat(mesh, mesh.worldNormal, light);
    
    // Subsurface scattering (wrap-lighting approximation): backlit direction
    // times a view-aligned falloff, modulated by a thickness map. Produces
    // the classic "glow through ears / leaves" look.
#ifndef XRENGINE_UBER_DISABLE_SUBSURFACE
    {
        vec3 distortedLight = normalize(-light.direction + mesh.worldNormal * _SSSDistortion);
        float backLight = max(0.0, dot(-mesh.worldNormal, light.direction));
        float viewWrap = pow(max(0.0, dot(mesh.viewDir, distortedLight)), max(_SSSPower, 0.001));
        float thickness = texture(_SSSThicknessMap, mesh.uv[0]).r;
        float sssStrength = backLight * viewWrap * _SSSScale * mix(1.0, thickness, 0.5);
        vec3 sss = _SSSColor.rgb * (light.color + light.indirectColor * _SSSAmbient) * sssStrength;
        fragData.finalColor += sss;
    }
#endif
    
    // Matcap compositing — can replace, multiply, and/or add independently.
#if !defined(XRENGINE_UBER_DISABLE_MATCAP) && defined(XRENGINE_UBER_DISABLE_LAYERED_MATCAP_RIM)
    {
        vec3 matcapColor = calculateMatcap(mesh, mesh.worldNormal, light, fragData.emission);
        fragData.finalColor = mix(fragData.finalColor, matcapColor, _MatcapReplace);
        fragData.finalColor *= mix(vec3(1.0), matcapColor, _MatcapMultiply);
        fragData.finalColor += matcapColor * _MatcapAdd;
    }
#endif
#ifndef XRENGINE_UBER_DISABLE_LAYERED_MATCAP_RIM
    if (_SourceMatcap <= 0.5)
        uberApplyMatcapSlots(fragData.finalColor, fragData.emission, matcapLightAdd, mesh, light);
#endif
    fragData.finalColor += matcapLightAdd;
    
    // Rim light. Also contributes to emission so it survives bloom.
#ifndef XRENGINE_UBER_DISABLE_RIM_LIGHTING
    vec3 rimColor = calculateRimLight(mesh, mesh.worldNormal, light);
    float rimStrength = saturate(_RimBlendStrength);
    if (_SourceRimSettings.x > 0.5) {
        rimStrength = 0.0;
    } else if (_RimBlendMode == 0) {
        fragData.finalColor = mix(fragData.finalColor, rimColor, rimStrength);
    } else if (_RimBlendMode == 2) {
        fragData.finalColor = mix(fragData.finalColor, fragData.finalColor * max(rimColor, vec3(0.0)), rimStrength);
    } else {
        fragData.finalColor += rimColor * rimStrength;
    }
    fragData.emission += rimColor * _RimEmission * rimStrength;
#endif
    vec3 secondRim = uberApplySecondRim(mesh, light);
    fragData.finalColor += secondRim;
    fragData.emission += secondRim;
    fragData.finalColor += uberApplyDepthRim(mesh);

    // Engine-authored glitter contributes to emission once.
#ifndef XRENGINE_UBER_DISABLE_GLITTER
    if (_SourceGlitter == 0) {
        vec2 glitterUV = mesh.uv[0] * max(_GlitterDensity, 0.001);
        float glitterNoise = hash21(floor(glitterUV) + vec2(u_Time * _GlitterSpeed));
        float nDotV = saturate(dot(mesh.worldNormal, mesh.viewDir));
        float angleMask = smoothstep(_GlitterMinAngle, _GlitterMaxAngle, 1.0 - nDotV);
        float glitterMask = texture(_GlitterMask, mesh.uv[0]).r;
        float glitterStrength = pow(glitterNoise, max(_GlitterSize, 0.001)) * _GlitterBrightness * angleMask * glitterMask;
        vec3 rainbow = hsvToRgb(vec3(fract(glitterNoise + u_Time * 0.05), 0.85, 1.0));
        vec3 glitterColor = mix(_GlitterColor.rgb, rainbow, saturate(_GlitterRainbow));
        vec3 glitter = glitterColor * glitterStrength;
        fragData.emission += glitter; // Glitter is emissive
    }
#endif
    
    // Flipbook (sprite-sheet) additive overlay.
#ifndef XRENGINE_UBER_DISABLE_FLIPBOOK
    {
        vec4 flipbookColor = simpleFlipbook(
            _FlipbookTexture,
            mesh.uv[0],
            int(max(_FlipbookColumns, 1.0)),
            int(max(_FlipbookRows, 1.0)),
            _FlipbookFrameRate,
            u_Time,
            _FlipbookFrame,
            _FlipbookManualFrame,
            _FlipbookBlendMode > 0.5,
            _FlipbookCrossfade
        );
        fragData.finalColor += flipbookColor.rgb * flipbookColor.a;
    }
#endif
    
    // Authored emission (texture * color * strength).
#if !defined(XRENGINE_UBER_DISABLE_EMISSION) && defined(XRENGINE_UBER_DISABLE_LAYERED_EMISSION)
    fragData.emission += calculateEmission(mesh);
#endif
    uberApplyEmissionSlots(fragData, mesh, light.color);
    uberApplyFlipbookArray(fragData, mesh);

    // Final composite: emission is always added on top of lit color so it
    // remains bright through tonemap and bloom.
    fragData.finalColor += fragData.emission;
    fragData.finalColor = uberApplyPostEffects(mesh, fragData.finalColor);
    fragData.alpha = uberApplyDepthIntersectionAlpha(fragData.alpha, mesh);

    // ---- 8. Final write -----------------------------------------------------
    vec4 shadedColor = vec4(fragData.finalColor, fragData.alpha);

    // Opaque and cutout passes must emit alpha=1. Partial alpha in those
    // passes would confuse MSAA resolve and downstream compositing (bloom /
    // tonemap), producing dark halos around cutout edges.
    if (_Mode == BLEND_MODE_OPAQUE || _Mode == BLEND_MODE_CUTOUT) {
        shadedColor.a = 1.0;
    } else {
#ifndef XRENGINE_UBER_DISABLE_SURFACE_EXTENSIONS
        shadedColor.rgb = mix(shadedColor.rgb, shadedColor.rgb * shadedColor.a, saturate(_AlphaPremultiply));
#endif
    }

    XRENGINE_WriteForwardFragment(shadedColor);
#endif
}
