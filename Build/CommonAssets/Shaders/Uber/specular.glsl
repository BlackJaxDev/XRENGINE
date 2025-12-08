// ===========================================
// Uber Shader - Specular Effects
// ===========================================
// Contains:
// - Stylized Specular (toon-friendly specular highlights)
// - Anisotropic Specular (hair/silk highlights)
// ===========================================

#ifndef TOON_SPECULAR_GLSL
#define TOON_SPECULAR_GLSL

// Stylized Specular Blend Modes
#define SPECULAR_BLEND_REPLACE   0
#define SPECULAR_BLEND_ADD       1
#define SPECULAR_BLEND_SCREEN    2
#define SPECULAR_BLEND_MULTIPLY  3

// ============================================
// Stylized Specular Data
// ============================================
struct StylizedSpecularData {
    bool enabled;
    vec3 specularTint;          // _HighColor
    float strength;              // _StylizedSpecularStrength
    
    // Layer 1
    float layer1Size;            // _HighColor_Power
    float layer1Feather;         // _StylizedSpecularFeather
    float layer1Strength;        // _Layer1Strength
    
    // Layer 2
    float layer2Size;            // _Layer2Size
    float layer2Feather;         // _StylizedSpecular2Feather
    float layer2Strength;        // _Layer2Strength
    
    // Mode
    bool isToonMode;             // !_Is_SpecularToHighColor (0 = Toon, 1 = Realistic)
    int blendMode;               // _Is_BlendAddToHiColor
    bool useLightColor;          // _UseLightColor
    
    // Advanced
    bool ignoreNormal;           // _StylizedSpecularIgnoreNormal
    bool ignoreShadow;           // _StylizedSpecularIgnoreShadow
    bool ignoreCastedShadows;    // _SSIgnoreCastedShadows
    bool invertMask;             // _StylizedSpecularInvertMask
    float maskLevel;             // _Tweak_HighColorMaskLevel
};

// ============================================
// Anisotropic Data
// ============================================
struct AnisotropicLayer {
    float power;         // _Aniso0Power / _Aniso1Power
    float strength;      // _Aniso0Strength / _Aniso1Strength
    float offset;        // _Aniso0Offset / _Aniso1Offset
    vec3 tint;           // _Aniso0Tint / _Aniso1Tint
    bool toonMode;       // _Aniso0ToonMode / _Aniso1ToonMode
    float toonEdge;      // _Aniso0Edge / _Aniso1Edge
    float toonBlur;      // _Aniso0Blur / _Aniso1Blur
    bool switchDirection;// _Aniso0SwitchDirection / _Aniso1SwitchDirection
    float offsetMapStrength; // _Aniso0OffsetMapStrength
};

struct AnisotropicData {
    bool enabled;
    AnisotropicLayer layer0;
    AnisotropicLayer layer1;
    bool hideInShadow;       // _AnisoHideInShadow
    bool useBaseColor;       // _AnisoUseBaseColor
    bool useLightColor;      // _AnisoUseLightColor
    float replaceAmount;     // _AnisoReplace
    float addAmount;         // _AnisoAdd
};

// ============================================
// Utility Functions
// ============================================

// Non-linear edge function for toon specular
float edgeNonLinear(float value, float edge, float feather) {
    float edgeMin = clamp(edge - feather * 0.5, 0.0, 1.0);
    float edgeMax = clamp(edge + feather * 0.5, 0.0, 1.0);
    return clamp((value - edgeMin) / max(edgeMax - edgeMin + fwidth(value), 0.0001), 0.0, 1.0);
}

// Anti-aliased edge feathering
float aaEdgeFeather(float value, float edge, float feather) {
    float edgeMin = clamp(edge - feather * 0.5, 0.0, 1.0);
    float edgeMax = clamp(edge + feather * 0.5, 0.0, 1.0);
    return clamp((value - edgeMin) / clamp(edgeMax - edgeMin + fwidth(value), 0.0001, 1.0), 0.0, 1.0);
}

// Screen blend mode
vec3 blendScreenSpec(vec3 base, vec3 blend) {
    return 1.0 - (1.0 - base) * (1.0 - blend);
}

// ============================================
// Stylized Specular Calculation
// ============================================
void applyStylizedSpecular(
    inout vec3 baseColor,
    inout vec3 lightAdd,
    StylizedSpecularData spec,
    ToonMesh mesh,
    ToonLight light,
    sampler2D specularMap,
    sampler2D specularMask,
    float time
) {
    if (!spec.enabled) return;
    
    // Calculate specular area (half-angle based)
    float specArea = 0.5 * light.nDotH + 0.5;
    
    // Sample specular map (color/intensity)
    vec3 specMapColor = texture(specularMap, mesh.uv[0]).rgb;
    
    // Calculate specular masks for both layers
    float specMask1 = 0.0;
    float specMask2 = 0.0;
    
    if (!spec.isToonMode) {
        // Realistic/smooth mode - use power curve
        specMask1 = pow(specArea, exp2(mix(11.0, 1.0, spec.layer1Size))) * spec.layer1Strength;
        specMask2 = pow(specArea, exp2(mix(11.0, 1.0, spec.layer2Size))) * spec.layer2Strength;
    } else {
        // Toon mode - use hard edge with feathering
        specMask1 = edgeNonLinear(specArea, 1.0 - pow(spec.layer1Size, 5.0), spec.layer1Feather) * spec.layer1Strength;
        specMask2 = edgeNonLinear(specArea, 1.0 - pow(spec.layer2Size, 5.0), spec.layer2Feather) * spec.layer2Strength;
    }
    
    // Sample and apply mask texture
    float maskValue = texture(specularMask, mesh.uv[0]).r;
    if (spec.invertMask) {
        maskValue = 1.0 - maskValue;
    }
    maskValue = clamp(maskValue + spec.maskLevel, 0.0, 1.0);
    
    // Combine masks with shadow
    float shadowMask = spec.ignoreShadow ? 1.0 : light.lightMap;
    float specMask = clamp(specMask1 + specMask2, 0.0, 1.0) * maskValue * shadowMask;
    
    // Calculate attenuation
    float normalFactor = spec.ignoreNormal ? 1.0 : clamp(light.nDotL, 0.0, 1.0);
    float shadowFactor = spec.ignoreShadow ? 1.0 : (spec.ignoreCastedShadows ? 1.0 : light.lightMap);
    float attenuation = min(normalFactor, shadowFactor);
    
    // Final specular mask
    float finalSpecMask = min(specMask, attenuation) * spec.strength;
    
    // Calculate specular color
    vec3 specColor = specMapColor * spec.specularTint;
    if (spec.useLightColor) {
        specColor *= light.color;
    }
    
    // Apply based on blend mode
    switch (spec.blendMode) {
        case SPECULAR_BLEND_REPLACE:
            baseColor = mix(baseColor, specColor, finalSpecMask);
            break;
        case SPECULAR_BLEND_ADD:
            lightAdd += max(specColor * finalSpecMask, vec3(0.0));
            break;
        case SPECULAR_BLEND_SCREEN:
            baseColor = mix(baseColor, blendScreenSpec(baseColor, specColor), finalSpecMask);
            break;
        case SPECULAR_BLEND_MULTIPLY:
            baseColor = mix(baseColor, baseColor * specColor, finalSpecMask);
            break;
    }
}

// ============================================
// Anisotropic Specular Calculation
// ============================================

// Core anisotropic calculation (Kajiya-Kay style)
float calculateAnisotropicTerm(
    vec3 binormal,
    float offset,
    vec3 normal,
    vec3 viewDir,
    vec3 lightDir,
    float exponent,
    float strength,
    float shadowMask
) {
    // Shift tangent/binormal by normal
    vec3 shiftedTangent = normalize(binormal + offset * normal);
    
    // Calculate half vector
    vec3 H = normalize(lightDir + viewDir);
    
    // Dot product between shifted tangent and half vector
    float dotTH = dot(shiftedTangent, H);
    float sinTH = sqrt(1.0 - dotTH * dotTH);
    
    // Directional attenuation (fade when light is behind)
    float dirAtten = smoothstep(-1.0, 0.0, dotTH);
    
    // Final specular term
    return clamp(dirAtten * pow(sinTH, exponent) * strength, 0.0, 1.0) * shadowMask;
}

void applyAnisotropics(
    inout vec3 baseColor,
    inout vec3 lightAdd,
    AnisotropicData aniso,
    ToonMesh mesh,
    ToonLight light,
    sampler2D anisoColorMap,
    float time
) {
    if (!aniso.enabled) return;
    
    // Sample color/offset map
    vec4 specMap = texture(anisoColorMap, mesh.uv[0]);
    
    // Calculate shadow mask
    float shadowMask = mix(1.0, light.lightMap, aniso.hideInShadow);
    
    // Get tangent and binormal
    vec3 tangent = mesh.TBN[0];
    vec3 binormal = mesh.TBN[1];
    
    // Calculate both layers
    float offset0 = aniso.layer0.offset + aniso.layer0.offsetMapStrength * specMap.a;
    float offset1 = aniso.layer1.offset + aniso.layer1.offsetMapStrength * specMap.a;
    
    // Choose direction (tangent or binormal)
    vec3 dir0 = aniso.layer0.switchDirection ? tangent : binormal;
    vec3 dir1 = aniso.layer1.switchDirection ? tangent : binormal;
    
    // Calculate anisotropic specular for both layers
    float spec0 = calculateAnisotropicTerm(
        dir0, offset0, mesh.worldNormal, mesh.viewDir, light.direction,
        aniso.layer0.power * 1000.0, aniso.layer0.strength, shadowMask
    );
    float spec1 = calculateAnisotropicTerm(
        dir1, offset1, mesh.worldNormal, mesh.viewDir, light.direction,
        aniso.layer1.power * 1000.0, aniso.layer1.strength, shadowMask
    );
    
    // Apply toon mode if enabled
    if (aniso.layer0.toonMode) {
        spec0 = aaEdgeFeather(spec0, aniso.layer0.toonEdge, aniso.layer0.toonBlur);
    }
    if (aniso.layer1.toonMode) {
        spec1 = aaEdgeFeather(spec1, aniso.layer1.toonEdge, aniso.layer1.toonBlur);
    }
    
    // Calculate colors for each layer
    vec3 spec0Color = specMap.rgb * aniso.layer0.tint;
    vec3 spec1Color = specMap.rgb * aniso.layer1.tint;
    
    // Apply base color modulation
    vec3 baseColorMod = aniso.useBaseColor ? baseColor : vec3(1.0);
    
    // Apply light color
    vec3 lightColorMod;
    if (aniso.useLightColor) {
        lightColorMod = light.color;
    } else {
        lightColorMod = vec3(dot(light.color, vec3(0.299, 0.587, 0.114)));
    }
    
    // Combine specular
    vec3 finalSpec = clamp(
        clamp(spec0 * spec0Color, 0.0, 1.0) + clamp(spec1 * spec1Color, 0.0, 1.0),
        0.0, 1.0
    ) * baseColorMod * lightColorMod;
    
    // Apply to base color (replace mode)
    vec3 originalBaseColor = baseColor;
    baseColor = mix(baseColor, spec1Color * baseColorMod * lightColorMod, aniso.replaceAmount * spec1);
    baseColor = mix(baseColor, spec0Color * baseColorMod * lightColorMod, aniso.replaceAmount * spec0);
    
    // Apply additive
    lightAdd += max(finalSpec * aniso.addAmount, vec3(0.0));
}

// ============================================
// Combined Specular Apply Function
// ============================================
void applySpecularEffects(
    inout vec3 baseColor,
    inout vec3 lightAdd,
    inout vec3 emission,
    ToonMesh mesh,
    ToonLight light,
    StylizedSpecularData stylizedSpec,
    AnisotropicData aniso,
    sampler2D specularMap,
    sampler2D specularMask,
    sampler2D anisoColorMap,
    float time
) {
    // Apply stylized specular
    applyStylizedSpecular(
        baseColor, lightAdd,
        stylizedSpec, mesh, light,
        specularMap, specularMask, time
    );
    
    // Apply anisotropics
    applyAnisotropics(
        baseColor, lightAdd,
        aniso, mesh, light,
        anisoColorMap, time
    );
}

#endif // TOON_SPECULAR_GLSL
