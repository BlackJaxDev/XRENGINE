// ===========================================
// Uber Shader - Back Face Rendering
// ===========================================
// Different rendering for back-facing polygons
// ===========================================

#ifndef TOON_BACKFACE_GLSL
#define TOON_BACKFACE_GLSL

// Back Face Modes
#define BACKFACE_MODE_DEFAULT       0   // Use front face settings
#define BACKFACE_MODE_COLOR         1   // Replace with solid color
#define BACKFACE_MODE_TEXTURE       2   // Use different texture
#define BACKFACE_MODE_FLIP_NORMALS  3   // Flip normals for back faces
#define BACKFACE_MODE_MIRROR_UV     4   // Mirror UVs

// ============================================
// Back Face Data Structure
// ============================================
struct BackFaceData {
    bool enabled;
    int mode;
    
    // Color mode
    vec4 color;
    float colorMix;             // How much to mix with original
    
    // Texture mode
    vec4 textureST;
    vec2 texturePan;
    int textureUV;
    
    // Detail overrides
    bool replaceEmission;
    vec3 emissionColor;
    float emissionStrength;
    
    // Hue shift
    bool hueShiftEnabled;
    float hueShift;
    
    // Alpha
    float alphaMod;
    
    // Normal handling
    bool flipNormals;
    float normalStrength;
};

// ============================================
// Detect Back Face
// ============================================
bool isBackFace(bool glFrontFacing) {
    return !glFrontFacing;
}

// ============================================
// Apply Back Face Color
// ============================================
vec3 applyBackFaceColor(
    vec3 baseColor,
    BackFaceData backface,
    bool isFrontFace
) {
    if (!backface.enabled || isFrontFace) {
        return baseColor;
    }
    
    vec3 result = baseColor;
    
    switch (backface.mode) {
        case BACKFACE_MODE_COLOR:
            result = mix(baseColor, backface.color.rgb, backface.colorMix);
            break;
            
        case BACKFACE_MODE_DEFAULT:
        default:
            // Keep original color
            break;
    }
    
    // Apply hue shift if enabled
    if (backface.hueShiftEnabled) {
        // Simple hue rotation
        vec3 k = vec3(0.57735);
        float cosAngle = cos(backface.hueShift * 6.28318);
        float sinAngle = sin(backface.hueShift * 6.28318);
        result = result * cosAngle + cross(k, result) * sinAngle + k * dot(k, result) * (1.0 - cosAngle);
    }
    
    return result;
}

// ============================================
// Apply Back Face Texture
// ============================================
vec4 sampleBackFaceTexture(
    sampler2D backfaceTex,
    BackFaceData backface,
    vec2 uv[4],
    float time,
    bool isFrontFace
) {
    if (!backface.enabled || isFrontFace || backface.mode != BACKFACE_MODE_TEXTURE) {
        return vec4(1.0);
    }
    
    vec2 texUV = uv[backface.textureUV] * backface.textureST.xy + backface.textureST.zw;
    texUV += backface.texturePan * time;
    
    return texture(backfaceTex, texUV);
}

// ============================================
// Apply Back Face Normal
// ============================================
vec3 applyBackFaceNormal(
    vec3 normal,
    BackFaceData backface,
    bool isFrontFace
) {
    if (!backface.enabled || isFrontFace) {
        return normal;
    }
    
    vec3 result = normal;
    
    // Flip normals for back faces
    if (backface.flipNormals) {
        result = -result;
    }
    
    // Reduce normal intensity
    result = mix(vec3(0.0, 0.0, 1.0), result, backface.normalStrength);
    result = normalize(result);
    
    return result;
}

// ============================================
// Get Back Face Emission
// ============================================
vec3 getBackFaceEmission(
    vec3 originalEmission,
    BackFaceData backface,
    bool isFrontFace
) {
    if (!backface.enabled || isFrontFace) {
        return originalEmission;
    }
    
    if (backface.replaceEmission) {
        return backface.emissionColor * backface.emissionStrength;
    }
    
    return originalEmission;
}

// ============================================
// Get Back Face Alpha
// ============================================
float applyBackFaceAlpha(
    float alpha,
    BackFaceData backface,
    bool isFrontFace
) {
    if (!backface.enabled || isFrontFace) {
        return alpha;
    }
    
    return clamp(alpha + backface.alphaMod, 0.0, 1.0);
}

// ============================================
// Get Back Face UV (for mirroring)
// ============================================
vec2 getBackFaceUV(
    vec2 uv,
    BackFaceData backface,
    bool isFrontFace
) {
    if (!backface.enabled || isFrontFace) {
        return uv;
    }
    
    if (backface.mode == BACKFACE_MODE_MIRROR_UV) {
        return vec2(1.0 - uv.x, uv.y);
    }
    
    return uv;
}

// ============================================
// Complete Back Face Application
// ============================================
void applyBackFace(
    inout vec3 baseColor,
    inout vec3 normal,
    inout vec3 emission,
    inout float alpha,
    BackFaceData backface,
    bool isFrontFace,
    sampler2D backfaceTex,
    vec2 uv[4],
    float time
) {
    if (!backface.enabled || isFrontFace) {
        return;
    }
    
    // Apply texture if in texture mode
    if (backface.mode == BACKFACE_MODE_TEXTURE) {
        vec4 texColor = sampleBackFaceTexture(backfaceTex, backface, uv, time, isFrontFace);
        baseColor = mix(baseColor, texColor.rgb * backface.color.rgb, backface.colorMix);
    } else {
        baseColor = applyBackFaceColor(baseColor, backface, isFrontFace);
    }
    
    // Apply normal modifications
    normal = applyBackFaceNormal(normal, backface, isFrontFace);
    
    // Apply emission
    emission = getBackFaceEmission(emission, backface, isFrontFace);
    
    // Apply alpha
    alpha = applyBackFaceAlpha(alpha, backface, isFrontFace);
}

#endif // TOON_BACKFACE_GLSL
