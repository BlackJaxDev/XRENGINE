// Uber Shader - Fragment Shader

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

#include "common.glsl"
#include "uniforms.glsl"
#undef PI
#pragma snippet "ForwardLighting"
#pragma snippet "AmbientOcclusionSampling"

// ============================================
// Fragment Inputs
// ============================================
layout(location = 0) in vec4 v_Uv01;
layout(location = 1) in vec4 v_Uv23;
layout(location = 2) in vec3 v_WorldPos;
layout(location = 3) in vec3 v_WorldNormal;
layout(location = 4) in vec3 v_WorldTangent;
layout(location = 5) in float v_TangentSign;
layout(location = 6) in vec4 v_VertexColor;
layout(location = 7) in vec3 v_LocalPos;
layout(location = 8) in vec3 v_ViewDir;

// ============================================
// Fragment Output
// ============================================
layout(location = 0) out vec4 FragColor;

// ============================================
// Internal Data Structures
// ============================================
struct ToonMesh {
    vec3 worldNormal;
    vec3 vertexNormal;
    vec3 tangentSpaceNormal;
    vec3 worldPos;
    vec3 localPos;
    vec3 viewDir;
    vec4 vertexColor;
    vec2 uv[4];
    float isFrontFace;
    mat3 TBN;
};

struct ToonLight {
    vec3 direction;
    vec3 color;
    float attenuation;
    vec3 indirectColor;
    float nDotL;
    float nDotV;
    float nDotH;
    float lDotH;
    vec3 halfDir;
    float lightMap;
    vec3 reflectionDir;
    vec3 finalLighting;
};

struct FragmentData {
    vec3 baseColor;
    vec3 finalColor;
    float alpha;
    vec3 emission;
};

struct PBRData {
    float metallic;
    float roughness;
    float perceptualRoughness;
    float reflectionMask;
    float specularMask;
    vec3 F0;
    vec3 diffuseColor;
    vec3 specularColor;
};

// ============================================
// UV Selection Helper
// ============================================
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
vec3 calculateNormal(ToonMesh mesh) {
    if (abs(_BumpScale) <= EPSILON) {
        return mesh.vertexNormal;
    }

    vec2 normalUV = transformUV(getUV(_BumpMapUV, mesh), _BumpMap_ST);
    normalUV = panUV(normalUV, _BumpMapPan, u_Time);
    
    vec4 normalTex = texture(_BumpMap, normalUV);
    vec3 tangentNormal = unpackNormal(normalTex, _BumpScale);
    
    // Transform from tangent space to world space
    vec3 worldNormal = normalize(mesh.TBN * tangentNormal);
    
    return worldNormal;
}

// ============================================
// Base Color Calculation
// ============================================
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
float calculateAlpha(vec4 baseColor, ToonMesh mesh) {
    float alpha = baseColor.a;
    
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
PBRData buildSurfacePbrData(vec2 uv, vec3 baseColor) {
    PBRData pbr;
    pbr.metallic = 0.0;
    pbr.perceptualRoughness = clamp(1.0 - _SpecularSmoothness, 0.04, 1.0);
    pbr.roughness = pbr.perceptualRoughness * pbr.perceptualRoughness;
    pbr.reflectionMask = 1.0;
    pbr.specularMask = 1.0;
    pbr.F0 = vec3(0.04);
    pbr.diffuseColor = baseColor;
    pbr.specularColor = vec3(1.0);
    return pbr;
}

float resolveSpecularIntensity(PBRData pbr) {
    return max(_SpecularStrength * pbr.specularMask, 0.0);
}

vec3 resolveSurfaceRms(PBRData pbr) {
    return vec3(
        clamp(pbr.perceptualRoughness, 0.0, 1.0),
        clamp(pbr.metallic, 0.0, 1.0),
        resolveSpecularIntensity(pbr));
}

float sampleMaterialAmbientOcclusion(ToonMesh mesh) {
    if (_LightDataAOStrengthR <= 0.0) {
        return 1.0;
    }

    vec2 aoUV = transformUV(getUV(_LightingAOMapsUV, mesh), _LightingAOMaps_ST);
    aoUV = panUV(aoUV, _LightingAOMapsPan, u_Time);
    float aoSample = texture(_LightingAOMaps, aoUV).r;
    return mix(1.0, aoSample, saturate(_LightDataAOStrengthR));
}

vec3 calculateForwardAmbientLighting(ToonMesh mesh, vec3 baseColor, vec3 normal, PBRData pbr, float ambientOcclusion) {
    return XRENGINE_CalculateAmbientPbr(
        normal,
        mesh.worldPos,
        baseColor,
        mesh.viewDir,
        resolveSurfaceRms(pbr),
        ambientOcclusion);
}

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
            i == 0);
    }

    return totalLight;
}

vec3 calculateForwardLocalLighting(ToonMesh mesh, vec3 normal, vec3 baseColor, PBRData pbr) {
    vec3 totalLight = vec3(0.0);
    vec3 rms = resolveSurfaceRms(pbr);

    if (ForwardPlusEnabled) {
        ivec2 tileCoord = ivec2(gl_FragCoord.xy) / ForwardPlusTileSize;
        int tileCountX = (int(ForwardPlusScreenSize.x) + ForwardPlusTileSize - 1) / ForwardPlusTileSize;
        int tileIndex = tileCoord.y * tileCountX + tileCoord.x;
        int baseIndex = tileIndex * ForwardPlusMaxLightsPerTile;

        for (int o = 0; o < ForwardPlusMaxLightsPerTile; ++o) {
            int lightIndex = ForwardPlusVisibleIndices[baseIndex + o];
            if (lightIndex < 0) {
                break;
            }

            ForwardPlusLocalLight l = ForwardPlusLocalLights[lightIndex];
            totalLight += (l.Color_Type.w < 0.5)
                ? XRENGINE_CalcForwardPlusPointLight(l, normal, mesh.worldPos, baseColor, rms, pbr.F0)
                : XRENGINE_CalcForwardPlusSpotLight(l, normal, mesh.worldPos, baseColor, rms, pbr.F0);
        }

        return totalLight;
    }

    for (int i = 0; i < PointLightCount; ++i) {
        totalLight += XRENGINE_CalcPointLight(i, PointLights[i], normal, mesh.worldPos, baseColor, rms, pbr.F0);
    }

    for (int i = 0; i < SpotLightCount; ++i) {
        totalLight += XRENGINE_CalcSpotLight(i, SpotLights[i], normal, mesh.worldPos, baseColor, rms, pbr.F0);
    }

    return totalLight;
}

vec3 calculateForwardDirectLighting(ToonMesh mesh, vec3 baseColor, vec3 normal, PBRData pbr, bool skipPrimaryDirectional) {
    return calculateForwardDirectionalLighting(mesh, normal, baseColor, pbr, skipPrimaryDirectional)
        + calculateForwardLocalLighting(mesh, normal, baseColor, pbr);
}

// ============================================
// Lighting Calculation
// ============================================
ToonLight calculateLighting(ToonMesh mesh, vec3 normal, vec3 indirectColor) {
    ToonLight light;
    
    if (DirLightCount > 0) {
        light.direction = normalize(-DirectionalLights[0].Direction);
        light.color = DirectionalLights[0].Base.Color * DirectionalLights[0].Base.DiffuseIntensity;
    } else {
        light.direction = vec3(0.0, 1.0, 0.0);
        light.color = vec3(0.0);
    }
    light.attenuation = 1.0;

#ifdef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
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
    
    // Indirect/ambient lighting
    light.indirectColor = indirectColor;
    if (_LightingIndirectUsesNormals > 0.0) {
        // Simple hemisphere lighting
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
    
    // Calculate light map based on mode
    float lightMap = light.nDotL;
    
    switch(_LightingMapMode) {
        case 0: // Poi Custom
            lightMap = light.nDotL * 0.5 + 0.5;
            break;
        case 1: // Normalized NDotL
            lightMap = light.nDotL * 0.5 + 0.5;
            break;
        case 2: // Saturated NDotL
            lightMap = saturate(light.nDotL);
            break;
        case 3: // Casted shadows only
            lightMap = 1.0;
            break;
    }
    
    light.lightMap = lightMap;
    
    return light;
#endif
}

// ============================================
// Shadow Map Sampling
// ============================================
float sampleShadowMap(vec3 worldPos, vec3 normal, float nDotL) {
    if (DirLightCount <= 0)
        return 1.0;
    return XRENGINE_ReadShadowMapDir(worldPos, normal, max(nDotL, 0.0));
}

// ============================================
// Shading Modes
// ============================================
vec3 applyShading(vec3 baseColor, ToonLight light, ToonMesh mesh, vec3 normal) {
#ifdef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
    return baseColor * (light.color * saturate(light.nDotL) + light.indirectColor);
#else
    if (_ShadingEnabled < 0.5) {
        // No shading - just apply light color
        return baseColor * (light.color + light.indirectColor);
    }
    
    // Sample directional light shadow map
    float shadowMapFactor = sampleShadowMap(mesh.worldPos, normal, light.nDotL);
    
    vec3 finalLight;
    float shadow = 1.0;
    
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
            // Flat lighting - minimal shading
            if (_ForceFlatRampedLightmap > 0.5) {
                shadow = step(0.5, light.lightMap);
            } else {
                shadow = light.lightMap;
            }
            finalLight = light.color * shadow + light.indirectColor * (1.0 - shadow);
            break;
        }
        
        case 6: // Realistic
        {
            // Simple Lambert
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
    
    // Apply shadow map factor from directional light
    shadow *= shadowMapFactor;
    
    // Apply shadow strength
    shadow = mix(1.0, shadow, _ShadowStrength);
    
    // Apply shadow color tint
    vec3 shadowTint = mix(_LightingShadowColor, vec3(1.0), shadow);
    
    // Combine
    vec3 result = baseColor * finalLight * shadowTint;
    
    // Apply lighting cap
    if (_LightingCapEnabled > 0.5) {
        float maxBrightness = _LightingCap;
        result = min(result, baseColor * maxBrightness);
    }
    
    // Apply minimum brightness
    float currentBrightness = luminance(result);
    if (currentBrightness < _LightingMinLightBrightness) {
        result *= _LightingMinLightBrightness / max(currentBrightness, EPSILON);
    }
    
    // Grayscale lighting option
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
vec3 calculateEmission(ToonMesh mesh, ToonLight light) {
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
}

// ============================================
// Matcap
// ============================================
vec3 calculateMatcap(ToonMesh mesh, vec3 normal, ToonLight light, inout vec3 emission) {
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
}

// ============================================
// Rim Lighting
// ============================================
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
void main() {
    // Initialize mesh data
    ToonMesh mesh;
    mesh.uv[0] = v_Uv01.xy;
    mesh.uv[1] = v_Uv01.zw;
    mesh.uv[2] = v_Uv23.xy;
    mesh.uv[3] = v_Uv23.zw;
    mesh.worldPos = v_WorldPos;
    mesh.localPos = v_LocalPos;
    mesh.vertexNormal = normalize(v_WorldNormal);
    mesh.vertexColor = v_VertexColor;
    mesh.viewDir = normalize(v_ViewDir);
    mesh.isFrontFace = gl_FrontFacing ? 1.0 : -1.0;
    
    // Flip normal for back faces
    mesh.vertexNormal *= mesh.isFrontFace;
    
    // Build TBN matrix
    vec3 bitangent = cross(mesh.vertexNormal, v_WorldTangent) * v_TangentSign;
    mesh.TBN = mat3(v_WorldTangent, bitangent, mesh.vertexNormal);
    
    // Apply parallax mapping to UVs (before any texture sampling)
#ifndef XRENGINE_UBER_DISABLE_PARALLAX
    if (_EnableParallax > 0.5) {
        int parallaxMode = int(_ParallaxMode);
        float parallaxValid = 1.0;
        vec2 parallaxUV = applyParallaxWithValidity(mesh.uv[0], mesh.worldPos, mesh.viewDir, mesh.TBN, parallaxMode, parallaxValid);

        // SPOM: discard fragments when the ray marches outside the base UV [0,1] bounds.
        if (parallaxMode == PARALLAX_SILHOUETTE_OCCLUSION && parallaxValid < 0.5) {
            discard;
        }

        mesh.uv[0] = parallaxUV;
    }
#endif
    
    // Calculate world normal (with normal mapping)
    mesh.worldNormal = calculateNormal(mesh);
    
    // Initialize fragment data
    FragmentData fragData;
    fragData.emission = vec3(0.0);
    
    // Calculate base color
    vec4 baseColor = calculateBaseColor(mesh);
    
    // Apply color adjustments
    baseColor.rgb = applyColorAdjustments(baseColor.rgb, mesh);
    
    // Calculate alpha
    fragData.alpha = calculateAlpha(baseColor, mesh);
    
    // Alpha cutoff (cutout mode)
    if (_Mode == BLEND_MODE_CUTOUT) {
        if (fragData.alpha < _Cutoff) {
            discard;
        }
    }
    
    fragData.baseColor = baseColor.rgb;
    
    // Apply dissolve effect (early out if dissolved)
#ifndef XRENGINE_UBER_DISABLE_DISSOLVE
    if (_EnableDissolve > 0.5) {
        if (applyDissolve(mesh.uv[0], mesh.worldPos, mesh.localPos, fragData.baseColor, fragData.emission, fragData.alpha)) {
            discard;
        }
    }
#endif
    
    // Apply detail textures (before lighting)
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
    
    // Calculate lighting
    float screenAmbientOcclusion = XRENGINE_SampleAmbientOcclusion();
    float materialAmbientOcclusion = sampleMaterialAmbientOcclusion(mesh);
    float combinedAmbientOcclusion = saturate(screenAmbientOcclusion * materialAmbientOcclusion);
    PBRData surfacePbr = buildSurfacePbrData(mesh.uv[0], fragData.baseColor);
    vec3 ambientLighting = calculateForwardAmbientLighting(mesh, fragData.baseColor, mesh.worldNormal, surfacePbr, combinedAmbientOcclusion);
    ToonLight light = calculateLighting(mesh, mesh.worldNormal, ambientLighting);
    
    // Apply back face coloring
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
    
    // Apply shading
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
    
    // Apply subsurface scattering
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
    
    // Calculate matcap
    vec3 matcapColor = calculateMatcap(mesh, mesh.worldNormal, light, fragData.emission);
    
    // Apply matcap blending
    if (_MatcapEnable > 0.5) {
        fragData.finalColor = mix(fragData.finalColor, matcapColor, _MatcapReplace);
        fragData.finalColor *= mix(vec3(1.0), matcapColor, _MatcapMultiply);
        fragData.finalColor += matcapColor * _MatcapAdd;
    }
    
    // Calculate rim lighting
#ifndef XRENGINE_UBER_DISABLE_RIM_LIGHTING
    vec3 rimColor = calculateRimLight(mesh, mesh.worldNormal, light);
    fragData.finalColor += rimColor;
    fragData.emission += rimColor * _RimEmission;
#endif
    
    // Apply glitter/sparkle
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
    
    // Apply flipbook animation (additive blend)
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
    
    // Calculate emission
    fragData.emission += calculateEmission(mesh, light);
    
    // Add emission to final color
    fragData.finalColor += fragData.emission;
    
    // Final output
    FragColor = vec4(fragData.finalColor, fragData.alpha);
    
    // Handle transparency modes — opaque and cutout should always output full alpha
    // so MSAA resolve and post-processing compositing don't see partial transparency.
    if (_Mode == BLEND_MODE_OPAQUE || _Mode == BLEND_MODE_CUTOUT) {
        FragColor.a = 1.0;
    }
}
