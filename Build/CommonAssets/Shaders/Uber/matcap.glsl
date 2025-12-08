// ===========================================
// Uber Shader - Matcap Module
// ===========================================
// Supports multiple matcap slots (up to 4) with:
// - Multiple UV modes (UTS, Top Pinch, Double Sided, Gradient)
// - Per-slot masks, tints, and blend modes
// - Emission contribution
// - Normal strength adjustment
// - Light-based masking
// ===========================================

#ifndef TOON_MATCAP_GLSL
#define TOON_MATCAP_GLSL

// Matcap UV Modes
#define MATCAP_UV_UTS           0   // Normal / UTS Style
#define MATCAP_UV_TOP_PINCH     1   // Top Pinch
#define MATCAP_UV_DOUBLE_SIDED  2   // Double Sided
#define MATCAP_UV_GRADIENT      3   // Gradient

// Matcap Blend Modes
#define MATCAP_BLEND_ADD        0
#define MATCAP_BLEND_MULTIPLY   1
#define MATCAP_BLEND_REPLACE    2
#define MATCAP_BLEND_MIXED      3
#define MATCAP_BLEND_SCREEN     4
#define MATCAP_BLEND_LIGHT_ADD  5

// ============================================
// Matcap Data Structure
// ============================================
struct MatcapData {
    bool enabled;
    vec4 color;              // Tint color with alpha
    float intensity;         // Overall intensity multiplier
    float emissionStrength;  // Emission contribution
    
    // UV Settings
    int uvMode;              // UV calculation mode
    float border;            // Border/size control
    float rotation;          // Rotation in turns (-1 to 1)
    vec2 pan;                // UV panning
    
    // Blending
    float addBlend;
    float multiplyBlend;
    float replaceBlend;
    float mixedBlend;
    float screenBlend;
    float lightAddBlend;
    
    // Mask settings
    int maskChannel;
    bool invertMask;
    float lightMask;         // How much light affects mask
    
    // Normal influence
    float normalStrength;    // 0-1, how much normal map affects matcap
    int normalSelect;        // Which normal to use (0=base, 1=detail)
    
    // UV Blending (for custom UV modes)
    int uvToBlend;
    vec2 uvBlendAmount;
};

// ============================================
// Matcap UV Calculation
// ============================================
vec2 calculateMatcapUV(
    MatcapData matcap,
    vec3 normal,
    vec3 viewDir,
    ToonMesh mesh,
    mat4 viewMatrix
) {
    vec2 matcapUV = vec2(0.5);
    
    switch (matcap.uvMode) {
        case MATCAP_UV_UTS:
        {
            // UTS Style - view-space normal with skew correction
            vec3 viewNormal = (viewMatrix * vec4(normal, 0.0)).xyz;
            vec3 NormalBlend_Detail = viewNormal * vec3(-1.0, -1.0, 1.0);
            vec3 NormalBlend_Base = (viewMatrix * vec4(viewDir, 0.0)).xyz * vec3(-1.0, -1.0, 1.0) + vec3(0.0, 0.0, 1.0);
            vec3 noSkewViewNormal = NormalBlend_Base * dot(NormalBlend_Base, NormalBlend_Detail) / NormalBlend_Base.z - NormalBlend_Detail;
            
            matcapUV = noSkewViewNormal.xy * matcap.border + 0.5;
            break;
        }
        case MATCAP_UV_TOP_PINCH:
        {
            // Top Pinch - world-space up projection
            vec3 worldViewUp = normalize(vec3(0.0, 1.0, 0.0) - viewDir * dot(viewDir, vec3(0.0, 1.0, 0.0)));
            vec3 worldViewRight = normalize(cross(viewDir, worldViewUp));
            matcapUV = vec2(dot(worldViewRight, normal), dot(worldViewUp, normal)) * matcap.border + 0.5;
            break;
        }
        case MATCAP_UV_DOUBLE_SIDED:
        {
            // Double Sided - separate left/right
            vec3 viewNormal = (viewMatrix * vec4(normal, 0.0)).xyz;
            vec3 viewUp = vec3(0.0, 1.0, 0.0);
            vec3 viewRight = vec3(1.0, 0.0, 0.0);
            
            matcapUV.x = dot(viewRight, viewNormal) * matcap.border + 0.5;
            matcapUV.y = dot(viewUp, viewNormal) * matcap.border + 0.5;
            break;
        }
        case MATCAP_UV_GRADIENT:
        {
            // Gradient mode - uses view angle
            float viewAngle = dot(normal, viewDir);
            matcapUV = vec2(viewAngle * matcap.border + 0.5, 0.5);
            break;
        }
    }
    
    // Apply rotation
    if (abs(matcap.rotation) > 0.001) {
        float angle = matcap.rotation * 3.14159265 * 2.0;
        float cs = cos(angle);
        float sn = sin(angle);
        vec2 centered = matcapUV - 0.5;
        matcapUV = vec2(
            centered.x * cs - centered.y * sn,
            centered.x * sn + centered.y * cs
        ) + 0.5;
    }
    
    // Apply panning
    matcapUV += matcap.pan;
    
    return matcapUV;
}

// ============================================
// Blend Matcap into Base Color
// ============================================
void blendMatcap(
    inout vec3 baseColor,
    inout vec3 lightAdd,
    inout vec3 emission,
    vec4 matcapColor,
    float matcapMask,
    MatcapData matcap,
    float lightMap
) {
    if (!matcap.enabled || matcapColor.a < 0.001) return;
    
    // Apply light-based masking
    if (matcap.lightMask > 0.0) {
        matcapMask *= mix(1.0, lightMap, matcap.lightMask);
    }
    
    // Apply color alpha to blending
    float effectiveAlpha = matcapColor.a * matcapMask;
    
    // Replace blend
    if (matcap.replaceBlend > 0.0) {
        baseColor = mix(baseColor, matcapColor.rgb, matcap.replaceBlend * effectiveAlpha * 0.999999);
    }
    
    // Multiply blend
    if (matcap.multiplyBlend > 0.0) {
        baseColor *= mix(vec3(1.0), matcapColor.rgb, matcap.multiplyBlend * effectiveAlpha);
    }
    
    // Add blend
    if (matcap.addBlend > 0.0) {
        baseColor += matcapColor.rgb * matcap.addBlend * effectiveAlpha;
    }
    
    // Screen blend
    if (matcap.screenBlend > 0.0) {
        vec3 screened = 1.0 - (1.0 - baseColor) * (1.0 - matcapColor.rgb);
        baseColor = mix(baseColor, screened, matcap.screenBlend * effectiveAlpha);
    }
    
    // Mixed blend (additive self-illumination style)
    if (matcap.mixedBlend > 0.0) {
        baseColor = mix(baseColor, baseColor + baseColor * matcapColor.rgb, matcap.mixedBlend * effectiveAlpha);
    }
    
    // Light add (adds to light contribution)
    if (matcap.lightAddBlend > 0.0) {
        lightAdd += matcapColor.rgb * matcap.lightAddBlend * effectiveAlpha;
    }
    
    // Emission
    if (matcap.emissionStrength > 0.0) {
        emission += matcapColor.rgb * matcap.emissionStrength * effectiveAlpha;
    }
}

// ============================================
// Sample and Apply Single Matcap
// ============================================
void applySingleMatcap(
    inout vec3 baseColor,
    inout vec3 lightAdd,
    inout vec3 emission,
    sampler2D matcapTex,
    sampler2D matcapMaskTex,
    MatcapData matcap,
    ToonMesh mesh,
    ToonLight light,
    mat4 viewMatrix
) {
    if (!matcap.enabled) return;
    
    // Blend normal based on strength
    vec3 effectiveNormal = mix(mesh.vertexNormal, mesh.worldNormal, matcap.normalStrength);
    
    // Calculate matcap UV
    vec2 matcapUV = calculateMatcapUV(matcap, effectiveNormal, mesh.viewDir, mesh, viewMatrix);
    
    // Sample matcap texture
    vec4 matcapColor = texture(matcapTex, matcapUV) * matcap.color * matcap.intensity;
    
    // Sample mask
    float matcapMask = texture(matcapMaskTex, mesh.uv[0])[matcap.maskChannel];
    if (matcap.invertMask) {
        matcapMask = 1.0 - matcapMask;
    }
    
    // Blend into base color
    blendMatcap(baseColor, lightAdd, emission, matcapColor, matcapMask, matcap, light.lightMap);
}

// ============================================
// Apply Multiple Matcaps (up to 4 slots)
// ============================================
void applyMatcaps(
    inout vec3 baseColor,
    inout vec3 lightAdd,
    inout vec3 emission,
    // Slot 0
    sampler2D matcapTex0,
    sampler2D matcapMask0,
    MatcapData matcap0,
    // Slot 1
    sampler2D matcapTex1,
    sampler2D matcapMask1,
    MatcapData matcap1,
    // Mesh and lighting
    ToonMesh mesh,
    ToonLight light,
    mat4 viewMatrix
) {
    // Apply first matcap
    applySingleMatcap(baseColor, lightAdd, emission, matcapTex0, matcapMask0, matcap0, mesh, light, viewMatrix);
    
    // Apply second matcap
    applySingleMatcap(baseColor, lightAdd, emission, matcapTex1, matcapMask1, matcap1, mesh, light, viewMatrix);
}

#endif // TOON_MATCAP_GLSL
