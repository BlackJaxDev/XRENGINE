// Uber Shader - Fragment Shader

#version 450 core

#include "common.glsl"
#include "uniforms.glsl"
#include "pbr.glsl"
#include "decals.glsl"
#include "matcap.glsl"
#include "emission.glsl"
#include "specular.glsl"
#include "details.glsl"
#include "backface.glsl"
#include "glitter.glsl"
#include "flipbook.glsl"
#include "subsurface.glsl"
#include "dissolve.glsl"
#include "parallax.glsl"

// ============================================
// Fragment Inputs
// ============================================
in VS_OUT {
    vec4 uv01;
    vec4 uv23;
    vec3 worldPos;
    vec3 worldNormal;
    vec3 worldTangent;
    float tangentSign;
    vec4 vertexColor;
    vec3 localPos;
    vec3 viewDir;
} fs_in;

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
// Lighting Calculation
// ============================================
ToonLight calculateLighting(ToonMesh mesh, vec3 normal) {
    ToonLight light;
    
    // Light direction (from surface to light)
    light.direction = normalize(-u_LightDirection);
    light.color = u_LightColor * u_LightIntensity;
    light.attenuation = 1.0;
    
    // Indirect/ambient lighting
    light.indirectColor = u_AmbientColor * u_AmbientIntensity;
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
}

// ============================================
// Shading Modes
// ============================================
vec3 applyShading(vec3 baseColor, ToonLight light, ToonMesh mesh) {
    if (_ShadingEnabled < 0.5) {
        // No shading - just apply light color
        return baseColor * (light.color + light.indirectColor);
    }
    
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
}

// ============================================
// Main Fragment Function
// ============================================
void main() {
    // Initialize mesh data
    ToonMesh mesh;
    mesh.uv[0] = fs_in.uv01.xy;
    mesh.uv[1] = fs_in.uv01.zw;
    mesh.uv[2] = fs_in.uv23.xy;
    mesh.uv[3] = fs_in.uv23.zw;
    mesh.worldPos = fs_in.worldPos;
    mesh.localPos = fs_in.localPos;
    mesh.vertexNormal = normalize(fs_in.worldNormal);
    mesh.vertexColor = fs_in.vertexColor;
    mesh.viewDir = normalize(fs_in.viewDir);
    mesh.isFrontFace = gl_FrontFacing ? 1.0 : -1.0;
    
    // Flip normal for back faces
    mesh.vertexNormal *= mesh.isFrontFace;
    
    // Build TBN matrix
    vec3 bitangent = cross(mesh.vertexNormal, fs_in.worldTangent) * fs_in.tangentSign;
    mesh.TBN = mat3(fs_in.worldTangent, bitangent, mesh.vertexNormal);
    
    // Apply parallax mapping to UVs (before any texture sampling)
    if (_EnableParallax > 0.5) {
        int parallaxMode = int(_ParallaxMode);
        mesh.uv[0] = applyParallax(mesh.uv[0], mesh.worldPos, mesh.viewDir, mesh.TBN, parallaxMode);
    }
    
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
    if (_EnableDissolve > 0.5) {
        if (applyDissolve(mesh.uv[0], mesh.worldPos, mesh.localPos, fragData.baseColor, fragData.emission, fragData.alpha)) {
            discard;
        }
    }
    
    // Apply detail textures (before lighting)
    if (_EnableDetailTextures > 0.5) {
        fragData.baseColor = applyDetailTextures(mesh.uv[0], fragData.baseColor);
        // Detail normal would be applied in calculateNormal if enabled
    }
    
    // Calculate lighting
    ToonLight light = calculateLighting(mesh, mesh.worldNormal);
    
    // Apply back face coloring
    if (_EnableBackFace > 0.5) {
        applyBackFace(mesh, fragData.baseColor, fragData.alpha, fragData.emission);
    }
    
    // Apply shading
    fragData.finalColor = applyShading(fragData.baseColor, light, mesh);
    
    // Apply subsurface scattering
    if (_EnableSSS > 0.5) {
        vec3 sss = calculateSSS(mesh.uv[0], mesh.worldNormal, mesh.viewDir, light.direction, light.color, light.indirectColor);
        fragData.finalColor += sss;
    }
    
    // Apply PBR / Reflections & Specular
    PBRLightData pbrLightData;
    pbrLightData.NdotL = light.nDotL;
    pbrLightData.NdotV = light.nDotV;
    pbrLightData.NdotH = light.nDotH;
    pbrLightData.LdotH = light.lDotH;
    pbrLightData.lightColor = light.color;
    pbrLightData.indirectColor = light.indirectColor;
    pbrLightData.reflectionDir = light.reflectionDir;
    pbrLightData.lightMap = light.lightMap;
    
    fragData.finalColor = applyPBR(fragData.finalColor, mesh.uv[0], fragData.baseColor, 
                                   mesh.worldNormal, mesh.viewDir, pbrLightData, fragData.emission);
    
    // Calculate matcap
    vec3 matcapColor = calculateMatcap(mesh, mesh.worldNormal, light, fragData.emission);
    
    // Apply matcap blending
    if (_MatcapEnable > 0.5) {
        fragData.finalColor = mix(fragData.finalColor, matcapColor, _MatcapReplace);
        fragData.finalColor *= mix(vec3(1.0), matcapColor, _MatcapMultiply);
        fragData.finalColor += matcapColor * _MatcapAdd;
    }
    
    // Calculate rim lighting
    vec3 rimColor = calculateRimLight(mesh, mesh.worldNormal, light);
    fragData.finalColor += rimColor;
    fragData.emission += rimColor * _RimEmission;
    
    // Apply glitter/sparkle
    if (_EnableGlitter > 0.5) {
        vec3 glitter = calculateGlitter(mesh.uv[0], mesh.worldPos, mesh.worldNormal, mesh.viewDir, light.direction);
        fragData.finalColor += glitter;
        fragData.emission += glitter; // Glitter is emissive
    }
    
    // Apply flipbook animation (additive blend)
    if (_EnableFlipbook > 0.5 && _FlipbookBlendMode > 0.5) {
        vec4 flipbookColor = calculateFlipbook(mesh.uv[0]);
        fragData.finalColor += flipbookColor.rgb * flipbookColor.a;
    }
    
    // Calculate emission
    fragData.emission += calculateEmission(mesh, light);
    
    // Add emission to final color
    fragData.finalColor += fragData.emission;
    
    // Final output
    FragColor = vec4(fragData.finalColor, fragData.alpha);
    
    // Handle transparency modes
    if (_Mode == BLEND_MODE_OPAQUE) {
        FragColor.a = 1.0;
    }
}
