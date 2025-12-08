// Uber Shader - PBR / Reflections & Specular
// Include file for BRDF-based lighting

#ifndef TOON_PBR_GLSL
#define TOON_PBR_GLSL

// ============================================
// PBR Uniforms
// ============================================
uniform float _PBRBRDF;
uniform float _PBRMetallicMultiplier;
uniform float _PBRRoughnessMultiplier;  // Actually "Smoothness"
uniform vec4 _PBRReflectionTint;
uniform vec4 _PBRSpecularTint;
uniform float _PBRReflectionStrength;
uniform float _PBRSpecularStrength;
uniform float _RefSpecFresnel;
uniform float _RefSpecFresnelAlpha;

uniform sampler2D _PBRMetallicMaps;
uniform vec4 _PBRMetallicMaps_ST;
uniform vec2 _PBRMetallicMapsPan;
uniform int _PBRMetallicMapsUV;
uniform int _PBRMetallicMapsMetallicChannel;    // default R (0)
uniform int _PBRMetallicMapsRoughnessChannel;   // default G (1) - smoothness
uniform int _PBRMetallicMapsReflectionMaskChannel; // default B (2)
uniform int _PBRMetallicMapsSpecularMaskChannel;   // default A (3)
uniform float _PBRMetallicMapInvert;
uniform float _PBRRoughnessMapInvert;
uniform float _PBRReflectionMaskInvert;
uniform float _PBRSpecularMaskInvert;

uniform samplerCube _PBRReflCube;
uniform float _PBRForceFallback;
uniform float _PBRLitFallback;

// 2nd Specular Layer
uniform float _Specular2ndLayer;
uniform float _PBRSpecularStrength2;
uniform float _PBRRoughnessMultiplier2;

// Advanced Controls
uniform float _PBRNormalSelect;
uniform float _PBRGSAAEnabled;
uniform float _GSAAVariance;
uniform float _GSAAThreshold;

// Environment reflection probe (from engine)
uniform samplerCube u_EnvironmentMap;
uniform float u_EnvironmentMapMipLevels;

// ============================================
// PBR Data Structure
// ============================================
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
// BRDF Functions
// ============================================

// GGX/Trowbridge-Reitz Normal Distribution Function
float D_GGX(float NdotH, float roughness) {
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH2 = NdotH * NdotH;
    float denom = NdotH2 * (a2 - 1.0) + 1.0;
    return a2 / (PI * denom * denom + EPSILON);
}

// Schlick-GGX Geometry Function (single direction)
float G_SchlickGGX(float NdotV, float roughness) {
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    return NdotV / (NdotV * (1.0 - k) + k + EPSILON);
}

// Smith's method for combined geometry term
float G_Smith(float NdotV, float NdotL, float roughness) {
    float ggx1 = G_SchlickGGX(NdotV, roughness);
    float ggx2 = G_SchlickGGX(NdotL, roughness);
    return ggx1 * ggx2;
}

// Schlick Fresnel approximation
vec3 F_Schlick(float cosTheta, vec3 F0) {
    float fresnel = pow(1.0 - cosTheta, 5.0);
    return F0 + (1.0 - F0) * fresnel;
}

// Schlick Fresnel with roughness for IBL
vec3 F_SchlickRoughness(float cosTheta, vec3 F0, float roughness) {
    float fresnel = pow(1.0 - cosTheta, 5.0);
    return F0 + (max(vec3(1.0 - roughness), F0) - F0) * fresnel;
}

// Environment BRDF approximation (split-sum LUT alternative)
// Karis approximation - avoids needing a precomputed LUT texture
vec2 envBRDFApprox(float NdotV, float roughness) {
    const vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
    const vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
    vec4 r = roughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * NdotV)) * r.x + r.y;
    return vec2(-1.04, 1.04) * a004 + r.zw;
}

// GSAA (Geometric Specular Anti-Aliasing)
// Reduces specular aliasing on high-frequency normals
float adjustRoughnessGeometry(float roughness, vec3 normal) {
    if (_PBRGSAAEnabled < 0.5) return roughness;
    
    // Screen-space derivatives of the normal
    vec3 vNormalWsDdx = dFdx(normal);
    vec3 vNormalWsDdy = dFdy(normal);
    float flGeometricRoughnessFactor = pow(saturate(max(dot(vNormalWsDdx, vNormalWsDdx), dot(vNormalWsDdy, vNormalWsDdy))), 0.333);
    flGeometricRoughnessFactor *= _GSAAVariance;
    
    return max(roughness, min(flGeometricRoughnessFactor, _GSAAThreshold));
}

// Calculate reflection with roughness-based mip level
vec3 sampleReflectionCubemap(samplerCube cube, vec3 reflDir, float roughness, float mipLevels) {
    float mipLevel = roughness * mipLevels;
    return textureLod(cube, reflDir, mipLevel).rgb;
}

// ============================================
// Channel Extraction Helper
// ============================================
float getChannel(vec4 tex, int channel) {
    if (channel == 0) return tex.r;
    if (channel == 1) return tex.g;
    if (channel == 2) return tex.b;
    if (channel == 3) return tex.a;
    return 1.0; // White (channel == 4)
}

// ============================================
// PBR Map Sampling
// ============================================
PBRData calculatePBRMaps(vec2 uv, vec3 baseColor) {
    PBRData pbr;
    
    // Sample packed maps
    vec2 metallicUV = uv * _PBRMetallicMaps_ST.xy + _PBRMetallicMaps_ST.zw;
    metallicUV += _PBRMetallicMapsPan * u_Time;
    vec4 metallicMaps = texture(_PBRMetallicMaps, metallicUV);
    
    // Extract channels
    float metallic = getChannel(metallicMaps, _PBRMetallicMapsMetallicChannel);
    float smoothness = getChannel(metallicMaps, _PBRMetallicMapsRoughnessChannel);
    float reflectionMask = getChannel(metallicMaps, _PBRMetallicMapsReflectionMaskChannel);
    float specularMask = getChannel(metallicMaps, _PBRMetallicMapsSpecularMaskChannel);
    
    // Apply inversions
    if (_PBRMetallicMapInvert > 0.5) metallic = 1.0 - metallic;
    if (_PBRRoughnessMapInvert > 0.5) smoothness = 1.0 - smoothness;
    if (_PBRReflectionMaskInvert > 0.5) reflectionMask = 1.0 - reflectionMask;
    if (_PBRSpecularMaskInvert > 0.5) specularMask = 1.0 - specularMask;
    
    // Apply multipliers
    pbr.metallic = metallic * _PBRMetallicMultiplier;
    pbr.perceptualRoughness = 1.0 - (smoothness * _PBRRoughnessMultiplier);
    pbr.roughness = pbr.perceptualRoughness * pbr.perceptualRoughness;
    pbr.reflectionMask = reflectionMask;
    pbr.specularMask = specularMask;
    
    // Calculate F0 (reflectance at normal incidence)
    // Dielectrics: ~0.04, Metals: albedo color
    vec3 dielectricF0 = vec3(0.04);
    pbr.F0 = mix(dielectricF0, baseColor, pbr.metallic);
    
    // Diffuse color is reduced by metallic (metals don't have diffuse)
    pbr.diffuseColor = baseColor * (1.0 - pbr.metallic);
    
    // Specular color for metals is the albedo
    pbr.specularColor = mix(vec3(1.0), baseColor, pbr.metallic);
    
    return pbr;
}

// Overload that takes a ToonMesh-like UV getter
PBRData calculatePBRMapsFromUV(vec2 uv[4], int uvIndex, vec3 baseColor) {
    vec2 selectedUV = uv[min(uvIndex, 3)];
    return calculatePBRMaps(selectedUV, baseColor);
}

// ============================================
// PBR Specular (Direct Lighting)
// ============================================
vec3 calculatePBRSpecular(float NdotL, float NdotV, float NdotH, float LdotH, 
                          PBRData pbr, vec3 lightColor, vec3 normal) {
    // Ensure proper ranges
    NdotL = max(NdotL, 0.0);
    NdotV = max(NdotV, 0.001);
    NdotH = max(NdotH, 0.0);
    LdotH = max(LdotH, 0.0);
    
    // Apply GSAA
    float adjustedRoughness = adjustRoughnessGeometry(pbr.roughness, normal);
    
    // Cook-Torrance BRDF
    float D = D_GGX(NdotH, adjustedRoughness);
    float G = G_Smith(NdotV, NdotL, adjustedRoughness);
    vec3 F = F_Schlick(LdotH, pbr.F0);
    
    vec3 numerator = D * G * F;
    float denominator = 4.0 * NdotV * NdotL + EPSILON;
    vec3 specular = numerator / denominator;
    
    // Apply light
    specular *= lightColor * NdotL;
    
    // Apply tint and strength
    specular *= _PBRSpecularTint.rgb * _PBRSpecularStrength;
    specular *= pbr.specularMask;
    
    return specular;
}

// 2nd Specular Layer (for multi-lobe specular)
vec3 calculatePBRSpecular2nd(float NdotL, float NdotV, float NdotH, float LdotH,
                              PBRData pbr, vec3 lightColor, vec3 normal) {
    if (_Specular2ndLayer < 0.5) return vec3(0.0);
    
    NdotL = max(NdotL, 0.0);
    NdotV = max(NdotV, 0.001);
    NdotH = max(NdotH, 0.0);
    LdotH = max(LdotH, 0.0);
    
    // Use different roughness for 2nd layer
    float roughness2 = 1.0 - _PBRRoughnessMultiplier2;
    roughness2 = roughness2 * roughness2;
    roughness2 = adjustRoughnessGeometry(roughness2, normal);
    
    float D = D_GGX(NdotH, roughness2);
    float G = G_Smith(NdotV, NdotL, roughness2);
    vec3 F = F_Schlick(LdotH, pbr.F0);
    
    vec3 numerator = D * G * F;
    float denominator = 4.0 * NdotV * NdotL + EPSILON;
    vec3 specular = numerator / denominator;
    
    specular *= lightColor * NdotL;
    specular *= _PBRSpecularTint.rgb * _PBRSpecularStrength2;
    specular *= pbr.specularMask;
    
    return specular;
}

// ============================================
// PBR Reflection (IBL / Environment)
// ============================================
vec3 calculatePBRReflection(float NdotV, vec3 reflectionDir, PBRData pbr, 
                            vec3 lightColor, vec3 indirectColor) {
    NdotV = max(NdotV, 0.001);
    
    // Fresnel for reflection
    vec3 F = F_SchlickRoughness(NdotV, pbr.F0, pbr.perceptualRoughness);
    
    // Apply fresnel intensity control
    vec3 fresnel = mix(pbr.F0, F, _RefSpecFresnel);
    
    // Sample reflection
    vec3 reflection;
    if (_PBRForceFallback > 0.5) {
        // Use fallback cubemap
        reflection = sampleReflectionCubemap(_PBRReflCube, reflectionDir, 
                                             pbr.perceptualRoughness, 8.0);
    } else {
        // Use environment map from engine
        reflection = sampleReflectionCubemap(u_EnvironmentMap, reflectionDir, 
                                             pbr.perceptualRoughness, u_EnvironmentMapMipLevels);
    }
    
    // Environment BRDF (split-sum approximation)
    vec2 envBRDF = envBRDFApprox(NdotV, pbr.perceptualRoughness);
    vec3 specularIBL = fresnel * envBRDF.x + envBRDF.y;
    
    // Combine
    reflection *= specularIBL;
    
    // Apply tint and strength
    reflection *= _PBRReflectionTint.rgb * _PBRReflectionStrength;
    reflection *= pbr.reflectionMask;
    
    // Lit fallback - modulate by light
    if (_PBRLitFallback > 0.5) {
        reflection *= lightColor + indirectColor;
    }
    
    return reflection;
}

// ============================================
// PBR Diffuse (Energy-Conserving)
// ============================================
vec3 calculatePBRDiffuse(float NdotL, float LdotH, PBRData pbr, 
                         vec3 lightColor, vec3 indirectColor) {
    NdotL = max(NdotL, 0.0);
    LdotH = max(LdotH, 0.0);
    
    // Energy conservation: diffuse is reduced by specular (Fresnel)
    vec3 F = F_Schlick(LdotH, pbr.F0);
    vec3 kD = (1.0 - F) * (1.0 - pbr.metallic);
    
    // Lambert diffuse
    vec3 diffuse = kD * pbr.diffuseColor / PI;
    diffuse *= lightColor * NdotL;
    
    // Add indirect diffuse (ambient)
    diffuse += pbr.diffuseColor * indirectColor * (1.0 - pbr.metallic);
    
    return diffuse;
}

// ============================================
// Main PBR Application Function
// ============================================

// Light data struct for PBR (matches what's typically passed from main shader)
struct PBRLightData {
    float NdotL;
    float NdotV;
    float NdotH;
    float LdotH;
    vec3 lightColor;
    vec3 indirectColor;
    vec3 reflectionDir;
    float lightMap;
};

vec3 applyPBR(vec3 shadedColor, vec2 uv, vec3 baseColor, vec3 normal, vec3 viewDir,
              PBRLightData lightData, inout vec3 emission) {
    if (_PBRBRDF < 0.5) return shadedColor;
    
    // Get PBR parameters from maps
    PBRData pbr = calculatePBRMaps(uv, baseColor);
    
    // Calculate PBR components
    vec3 diffuse = calculatePBRDiffuse(lightData.NdotL, lightData.LdotH, pbr, 
                                       lightData.lightColor, lightData.indirectColor);
    
    vec3 specular = calculatePBRSpecular(lightData.NdotL, lightData.NdotV, 
                                         lightData.NdotH, lightData.LdotH,
                                         pbr, lightData.lightColor, normal);
    
    vec3 specular2 = calculatePBRSpecular2nd(lightData.NdotL, lightData.NdotV,
                                              lightData.NdotH, lightData.LdotH,
                                              pbr, lightData.lightColor, normal);
    
    vec3 reflection = calculatePBRReflection(lightData.NdotV, lightData.reflectionDir, pbr,
                                             lightData.lightColor, lightData.indirectColor);
    
    // Full PBR result
    vec3 pbrResult = diffuse + specular + specular2 + reflection;
    
    // Blend between toon shading and PBR based on metallic and enable amount
    // For non-metallic surfaces, we can layer specular/reflection on top of toon
    // For metallic surfaces, use full PBR
    float pbrBlend = max(pbr.metallic, 0.5 * _PBRBRDF);
    vec3 finalColor = mix(shadedColor, pbrResult, pbrBlend);
    
    // Add specular highlights on top for hybrid toon+PBR look (non-metallic)
    finalColor += (specular + specular2) * (1.0 - pbr.metallic);
    finalColor += reflection;
    
    return finalColor;
}

// Simplified overload for when you have ToonLight-style struct
// This assumes the calling shader has computed the necessary dot products
vec3 applyPBRSimple(vec3 shadedColor, vec2 uv, vec3 baseColor, vec3 normal, vec3 viewDir,
                    vec3 lightDir, vec3 lightColor, vec3 indirectColor, inout vec3 emission) {
    if (_PBRBRDF < 0.5) return shadedColor;
    
    // Calculate dot products
    vec3 halfDir = normalize(lightDir + viewDir);
    float NdotL = dot(normal, lightDir);
    float NdotV = dot(normal, viewDir);
    float NdotH = dot(normal, halfDir);
    float LdotH = dot(lightDir, halfDir);
    vec3 reflectionDir = reflect(-viewDir, normal);
    
    // Build light data
    PBRLightData lightData;
    lightData.NdotL = NdotL;
    lightData.NdotV = NdotV;
    lightData.NdotH = NdotH;
    lightData.LdotH = LdotH;
    lightData.lightColor = lightColor;
    lightData.indirectColor = indirectColor;
    lightData.reflectionDir = reflectionDir;
    lightData.lightMap = NdotL * 0.5 + 0.5;
    
    return applyPBR(shadedColor, uv, baseColor, normal, viewDir, lightData, emission);
}

#endif // TOON_PBR_GLSL
