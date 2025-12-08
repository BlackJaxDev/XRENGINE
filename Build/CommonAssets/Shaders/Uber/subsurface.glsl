// ===========================================
// Uber Shader - Subsurface Scattering
// ===========================================
// Light transmission through thin/translucent surfaces
// ===========================================

#ifndef TOON_SUBSURFACE_GLSL
#define TOON_SUBSURFACE_GLSL

// SSS Modes
#define SSS_MODE_SIMPLE         0   // Simple backlight approximation
#define SSS_MODE_WRAPPED        1   // Wrapped diffuse SSS
#define SSS_MODE_TRANSMITTANCE  2   // Transmittance-based (thickness map)
#define SSS_MODE_PREINTEGRATED  3   // Pre-integrated SSS (lookup texture)

// ============================================
// SSS Data Structure
// ============================================
struct SubsurfaceData {
    bool enabled;
    vec3 color;                 // SSS tint color
    float strength;             // Overall SSS intensity
    
    // Mode
    int mode;
    
    // Thickness
    bool useThicknessMap;
    int thicknessMapChannel;
    float thicknessMapInvert;
    float thickness;            // Base thickness (0-1, 0 = thin, 1 = thick)
    float thicknessPower;       // Thickness falloff
    
    // Scattering
    float distortion;           // Normal distortion for view-dependent SSS
    float power;                // Falloff power
    float scale;                // Intensity scale
    float ambient;              // Ambient SSS contribution
    
    // Light interaction
    float lightInfluence;       // How much direct light affects SSS
    float shadowInfluence;      // How much shadows reduce SSS
    
    // Toon mode
    bool toonMode;
    float toonThreshold;
    float toonSmoothing;
    
    // Mask
    bool useMask;
    int maskChannel;
};

// ============================================
// SSS Calculation Functions
// ============================================

// Simple backlight SSS
vec3 calculateSSS_Simple(
    vec3 normal,
    vec3 viewDir,
    vec3 lightDir,
    vec3 lightColor,
    SubsurfaceData sss,
    float thickness
) {
    // View-dependent backlight
    vec3 H = normalize(lightDir + normal * sss.distortion);
    float VdotH = pow(clamp(dot(viewDir, -H), 0.0, 1.0), sss.power) * sss.scale;
    
    // Thickness attenuation
    float thicknessAtten = pow(1.0 - thickness, sss.thicknessPower);
    
    return sss.color * lightColor * VdotH * thicknessAtten * sss.strength;
}

// Wrapped diffuse SSS
vec3 calculateSSS_Wrapped(
    vec3 normal,
    vec3 lightDir,
    vec3 lightColor,
    SubsurfaceData sss,
    float thickness
) {
    // Wrapped diffuse with negative wrap
    float NdotL = dot(normal, lightDir);
    float wrap = -0.5; // Allow light from behind
    float wrappedDiffuse = max(0.0, (NdotL + wrap) / (1.0 + wrap));
    
    // Back light contribution
    float backLight = max(0.0, -NdotL) * (1.0 - thickness);
    
    // Combine
    float sssContrib = wrappedDiffuse + backLight * sss.strength;
    
    return sss.color * lightColor * sssContrib * sss.strength;
}

// Transmittance-based SSS (physically-based approximation)
vec3 calculateSSS_Transmittance(
    vec3 normal,
    vec3 viewDir,
    vec3 lightDir,
    vec3 lightColor,
    SubsurfaceData sss,
    float thickness
) {
    // Calculate transmittance based on thickness
    vec3 transmittance = exp(-thickness * sss.thicknessPower * (1.0 - sss.color));
    
    // View-dependent component
    float VdotL = dot(viewDir, -lightDir);
    float forward = pow(max(0.0, VdotL), sss.power);
    
    // Distorted normal backlight
    vec3 distortedNormal = normalize(normal * sss.distortion + -lightDir);
    float backlight = max(0.0, dot(viewDir, distortedNormal));
    
    // Combine
    vec3 sssContrib = transmittance * (forward + backlight * sss.scale) * sss.strength;
    
    return sss.color * lightColor * sssContrib;
}

// ============================================
// Toon Mode SSS
// ============================================

vec3 applyToonSSS(vec3 sssColor, float intensity, SubsurfaceData sss) {
    if (!sss.toonMode) {
        return sssColor;
    }
    
    // Hard edge with smoothing
    float toonFactor = smoothstep(
        sss.toonThreshold - sss.toonSmoothing,
        sss.toonThreshold + sss.toonSmoothing,
        intensity
    );
    
    return sssColor * toonFactor;
}

// ============================================
// Main SSS Function
// ============================================

vec3 calculateSubsurface(
    SubsurfaceData sss,
    vec3 normal,
    vec3 viewDir,
    vec3 lightDir,
    vec3 lightColor,
    float shadow,
    float thicknessSample
) {
    if (!sss.enabled) {
        return vec3(0.0);
    }
    
    // Calculate effective thickness
    float thickness = sss.thickness;
    if (sss.useThicknessMap) {
        float mapThickness = sss.thicknessMapInvert > 0.5 ? 1.0 - thicknessSample : thicknessSample;
        thickness *= mapThickness;
    }
    
    vec3 sssContrib = vec3(0.0);
    
    // Calculate SSS based on mode
    switch (sss.mode) {
        case SSS_MODE_SIMPLE:
            sssContrib = calculateSSS_Simple(normal, viewDir, lightDir, lightColor, sss, thickness);
            break;
            
        case SSS_MODE_WRAPPED:
            sssContrib = calculateSSS_Wrapped(normal, lightDir, lightColor, sss, thickness);
            break;
            
        case SSS_MODE_TRANSMITTANCE:
            sssContrib = calculateSSS_Transmittance(normal, viewDir, lightDir, lightColor, sss, thickness);
            break;
            
        default:
            sssContrib = calculateSSS_Simple(normal, viewDir, lightDir, lightColor, sss, thickness);
    }
    
    // Apply toon mode
    float intensity = max(max(sssContrib.r, sssContrib.g), sssContrib.b);
    sssContrib = applyToonSSS(sssContrib, intensity, sss);
    
    // Apply shadow influence
    sssContrib *= mix(1.0, shadow, sss.shadowInfluence);
    
    // Add ambient SSS
    sssContrib += sss.color * sss.ambient * (1.0 - thickness);
    
    return sssContrib;
}

// ============================================
// Apply SSS to Final Color
// ============================================

void applySubsurface(
    inout vec3 finalColor,
    sampler2D thicknessMap,
    sampler2D sssMask,
    SubsurfaceData sss,
    vec2 uv[4],
    vec3 normal,
    vec3 viewDir,
    vec3 lightDir,
    vec3 lightColor,
    float shadow
) {
    if (!sss.enabled) return;
    
    // Sample thickness
    float thicknessSample = sss.thickness;
    if (sss.useThicknessMap) {
        thicknessSample = texture(thicknessMap, uv[0])[sss.thicknessMapChannel];
    }
    
    // Sample mask
    float mask = 1.0;
    if (sss.useMask) {
        mask = texture(sssMask, uv[0])[sss.maskChannel];
    }
    
    // Calculate SSS
    vec3 sssContrib = calculateSubsurface(
        sss, normal, viewDir, lightDir, lightColor, shadow, thicknessSample
    );
    
    // Apply mask and add to final color
    finalColor += sssContrib * mask;
}

// ============================================
// Skin SSS Approximation
// ============================================

vec3 calculateSkinSSS(
    vec3 normal,
    vec3 viewDir,
    vec3 lightDir,
    vec3 lightColor,
    vec3 skinColor,
    float curvature,
    float shadow
) {
    // Curvature-based scattering (higher curvature = more scattering)
    float NdotL = dot(normal, lightDir);
    
    // Red scattering (deeper penetration)
    float redScatter = max(0.0, -NdotL * 0.5 + 0.5) * curvature;
    
    // Orange/yellow rim (shallower)
    float fresnel = 1.0 - max(0.0, dot(normal, viewDir));
    float rimScatter = fresnel * fresnel * curvature * 0.5;
    
    // Combine with skin tone
    vec3 sssColor = skinColor * vec3(1.0, 0.4, 0.3); // Red-shifted for skin
    vec3 sssContrib = sssColor * lightColor * (redScatter + rimScatter);
    
    return sssContrib * shadow;
}

#endif // TOON_SUBSURFACE_GLSL
