// ===========================================
// Uber Shader - Detail Textures
// ===========================================
// Secondary detail textures for adding surface detail
// ===========================================

#ifndef TOON_DETAILS_GLSL
#define TOON_DETAILS_GLSL

// Detail Blend Modes
#define DETAIL_BLEND_REPLACE    0
#define DETAIL_BLEND_MULTIPLY   1
#define DETAIL_BLEND_ADD        2
#define DETAIL_BLEND_SCREEN     3
#define DETAIL_BLEND_OVERLAY    4
#define DETAIL_BLEND_MIXED      5

// ============================================
// Detail Data Structure
// ============================================
struct DetailData {
    bool enabled;
    
    // Detail Texture
    vec4 textureST;
    vec2 texturePan;
    int textureUV;
    vec3 tint;
    float intensity;
    float brightness;
    int blendMode;
    
    // Detail Normal
    bool normalEnabled;
    vec4 normalST;
    vec2 normalPan;
    int normalUV;
    float normalScale;
    
    // Mask
    bool useMask;
    vec4 maskST;
    int maskUV;
    int maskChannel;
    float maskInvert;
};

// ============================================
// Detail Blending Functions
// ============================================

vec3 blendDetail(vec3 base, vec3 detail, int blendMode, float intensity) {
    vec3 result;
    
    switch (blendMode) {
        case DETAIL_BLEND_REPLACE:
            result = detail;
            break;
            
        case DETAIL_BLEND_MULTIPLY:
            result = base * detail;
            break;
            
        case DETAIL_BLEND_ADD:
            result = base + detail;
            break;
            
        case DETAIL_BLEND_SCREEN:
            result = 1.0 - (1.0 - base) * (1.0 - detail);
            break;
            
        case DETAIL_BLEND_OVERLAY:
            result = vec3(
                base.r < 0.5 ? 2.0 * base.r * detail.r : 1.0 - 2.0 * (1.0 - base.r) * (1.0 - detail.r),
                base.g < 0.5 ? 2.0 * base.g * detail.g : 1.0 - 2.0 * (1.0 - base.g) * (1.0 - detail.g),
                base.b < 0.5 ? 2.0 * base.b * detail.b : 1.0 - 2.0 * (1.0 - base.b) * (1.0 - detail.b)
            );
            break;
            
        case DETAIL_BLEND_MIXED:
            // Unity's detail map behavior (around 0.5 = no change)
            result = base * (detail * 2.0);
            break;
            
        default:
            result = base * detail;
    }
    
    return mix(base, result, intensity);
}

// ============================================
// Detail Normal Blending
// ============================================

// Blend two normals using Reoriented Normal Mapping (RNM)
vec3 blendNormalsRNM(vec3 baseNormal, vec3 detailNormal) {
    vec3 t = baseNormal + vec3(0.0, 0.0, 1.0);
    vec3 u = detailNormal * vec3(-1.0, -1.0, 1.0);
    return normalize(t * dot(t, u) - u * t.z);
}

// Blend normals using UDN method (simpler)
vec3 blendNormalsUDN(vec3 baseNormal, vec3 detailNormal) {
    return normalize(vec3(
        baseNormal.xy + detailNormal.xy,
        baseNormal.z * detailNormal.z
    ));
}

// Simple linear blend
vec3 blendNormalsLinear(vec3 baseNormal, vec3 detailNormal, float strength) {
    return normalize(mix(baseNormal, detailNormal, strength));
}

// ============================================
// Apply Detail Texture
// ============================================

void applyDetailTexture(
    inout vec3 baseColor,
    sampler2D detailTex,
    sampler2D detailMask,
    DetailData detail,
    vec2 uv[4],
    float time
) {
    if (!detail.enabled) return;
    
    // Get detail UV
    vec2 detailUV = uv[detail.textureUV] * detail.textureST.xy + detail.textureST.zw;
    detailUV += detail.texturePan * time;
    
    // Sample detail texture
    vec3 detailColor = texture(detailTex, detailUV).rgb;
    detailColor *= detail.tint;
    detailColor += detail.brightness;
    
    // Get mask value
    float mask = 1.0;
    if (detail.useMask) {
        vec2 maskUV = uv[detail.maskUV] * detail.maskST.xy + detail.maskST.zw;
        mask = texture(detailMask, maskUV)[detail.maskChannel];
        if (detail.maskInvert > 0.5) {
            mask = 1.0 - mask;
        }
    }
    
    // Apply detail with mask
    float effectiveIntensity = detail.intensity * mask;
    baseColor = blendDetail(baseColor, detailColor, detail.blendMode, effectiveIntensity);
}

// ============================================
// Apply Detail Normal
// ============================================

vec3 applyDetailNormal(
    vec3 baseNormal,
    sampler2D detailNormalMap,
    sampler2D detailMask,
    DetailData detail,
    vec2 uv[4],
    mat3 TBN,
    float time
) {
    if (!detail.enabled || !detail.normalEnabled) return baseNormal;
    
    // Get detail normal UV
    vec2 detailUV = uv[detail.normalUV] * detail.normalST.xy + detail.normalST.zw;
    detailUV += detail.normalPan * time;
    
    // Sample detail normal
    vec4 detailNormalTex = texture(detailNormalMap, detailUV);
    vec3 detailNormal;
    detailNormal.xy = (detailNormalTex.xy * 2.0 - 1.0) * detail.normalScale;
    detailNormal.z = sqrt(1.0 - clamp(dot(detailNormal.xy, detailNormal.xy), 0.0, 1.0));
    
    // Transform to world space
    vec3 detailWorldNormal = normalize(TBN * detailNormal);
    
    // Get mask value
    float mask = 1.0;
    if (detail.useMask) {
        vec2 maskUV = uv[detail.maskUV] * detail.maskST.xy + detail.maskST.zw;
        mask = texture(detailMask, maskUV)[detail.maskChannel];
        if (detail.maskInvert > 0.5) {
            mask = 1.0 - mask;
        }
    }
    
    // Blend normals
    return blendNormalsRNM(baseNormal, mix(vec3(0.0, 0.0, 1.0), detailWorldNormal, mask));
}

#endif // TOON_DETAILS_GLSL
