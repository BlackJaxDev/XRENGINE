// ===========================================
// Uber Shader - Flipbook Animation
// ===========================================
// Animated texture sheets (sprite sheets)
// ===========================================

#ifndef TOON_FLIPBOOK_GLSL
#define TOON_FLIPBOOK_GLSL

// Flipbook Blend Modes
#define FLIPBOOK_BLEND_REPLACE      0
#define FLIPBOOK_BLEND_ADD          1
#define FLIPBOOK_BLEND_MULTIPLY     2
#define FLIPBOOK_BLEND_SCREEN       3
#define FLIPBOOK_BLEND_OVERLAY      4

// ============================================
// Flipbook Data Structure
// ============================================
struct FlipbookData {
    bool enabled;
    
    // Grid settings
    int columns;                // Number of columns in sheet
    int rows;                   // Number of rows in sheet
    int totalFrames;            // Total frames (can be less than cols*rows)
    
    // Animation
    float fps;                  // Frames per second
    float currentFrame;         // Manual frame control (if manualFrame enabled)
    bool manualFrame;           // Use manual frame instead of time-based
    bool crossfade;             // Smooth transition between frames
    float crossfadeStrength;    // 0-1 crossfade amount
    
    // UV settings
    int uvChannel;
    vec4 textureST;
    vec2 texturePan;
    
    // Blending
    vec4 tint;
    int blendMode;
    float intensity;
    float emission;
    
    // Alpha
    float alphaCutoff;
    bool useAlpha;
    int alphaBlendMode;         // 0: Replace, 1: Multiply, 2: Add
    
    // Mask
    bool useMask;
    int maskChannel;
};

// ============================================
// Calculate Flipbook UV
// ============================================

// Get UV for a specific frame
vec2 getFlipbookFrameUV(vec2 uv, int frame, int columns, int rows) {
    // Wrap frame to valid range
    int totalCells = columns * rows;
    frame = frame % totalCells;
    
    // Calculate row and column
    int col = frame % columns;
    int row = frame / columns;
    
    // Calculate cell size
    vec2 cellSize = vec2(1.0 / float(columns), 1.0 / float(rows));
    
    // Calculate cell offset
    vec2 cellOffset = vec2(float(col), float(rows - 1 - row)) * cellSize;
    
    // Apply to UV
    return fract(uv) * cellSize + cellOffset;
}

// Get UV with crossfade info
struct FlipbookUVResult {
    vec2 uv1;           // Current frame UV
    vec2 uv2;           // Next frame UV
    float blend;        // Blend factor between frames
};

FlipbookUVResult getFlipbookUVCrossfade(
    vec2 uv,
    FlipbookData flipbook,
    float time
) {
    FlipbookUVResult result;
    
    // Calculate current frame
    float frameFloat;
    if (flipbook.manualFrame) {
        frameFloat = flipbook.currentFrame;
    } else {
        frameFloat = time * flipbook.fps;
    }
    
    int frame1 = int(floor(frameFloat)) % flipbook.totalFrames;
    int frame2 = (frame1 + 1) % flipbook.totalFrames;
    
    // Calculate blend factor
    result.blend = flipbook.crossfade ? fract(frameFloat) * flipbook.crossfadeStrength : 0.0;
    
    // Get UVs for both frames
    result.uv1 = getFlipbookFrameUV(uv, frame1, flipbook.columns, flipbook.rows);
    result.uv2 = getFlipbookFrameUV(uv, frame2, flipbook.columns, flipbook.rows);
    
    return result;
}

// ============================================
// Sample Flipbook Texture
// ============================================

vec4 sampleFlipbook(
    sampler2D flipbookTex,
    FlipbookData flipbook,
    vec2 baseUV,
    float time
) {
    if (!flipbook.enabled) {
        return vec4(0.0);
    }
    
    // Apply texture transform
    vec2 uv = baseUV * flipbook.textureST.xy + flipbook.textureST.zw;
    uv += flipbook.texturePan * time;
    
    // Get flipbook UVs
    FlipbookUVResult uvResult = getFlipbookUVCrossfade(uv, flipbook, time);
    
    // Sample textures
    vec4 color1 = texture(flipbookTex, uvResult.uv1);
    vec4 color2 = texture(flipbookTex, uvResult.uv2);
    
    // Blend frames
    vec4 result = mix(color1, color2, uvResult.blend);
    
    // Apply tint
    result *= flipbook.tint;
    
    return result;
}

// ============================================
// Blend Functions
// ============================================

vec3 blendFlipbook(vec3 base, vec3 flipbook, int blendMode, float intensity, float alpha) {
    vec3 result;
    float effectiveIntensity = intensity * alpha;
    
    switch (blendMode) {
        case FLIPBOOK_BLEND_REPLACE:
            result = mix(base, flipbook, effectiveIntensity);
            break;
            
        case FLIPBOOK_BLEND_ADD:
            result = base + flipbook * effectiveIntensity;
            break;
            
        case FLIPBOOK_BLEND_MULTIPLY:
            result = mix(base, base * flipbook, effectiveIntensity);
            break;
            
        case FLIPBOOK_BLEND_SCREEN:
            vec3 screened = 1.0 - (1.0 - base) * (1.0 - flipbook);
            result = mix(base, screened, effectiveIntensity);
            break;
            
        case FLIPBOOK_BLEND_OVERLAY:
            vec3 overlayed = vec3(
                base.r < 0.5 ? 2.0 * base.r * flipbook.r : 1.0 - 2.0 * (1.0 - base.r) * (1.0 - flipbook.r),
                base.g < 0.5 ? 2.0 * base.g * flipbook.g : 1.0 - 2.0 * (1.0 - base.g) * (1.0 - flipbook.g),
                base.b < 0.5 ? 2.0 * base.b * flipbook.b : 1.0 - 2.0 * (1.0 - base.b) * (1.0 - flipbook.b)
            );
            result = mix(base, overlayed, effectiveIntensity);
            break;
            
        default:
            result = mix(base, flipbook, effectiveIntensity);
    }
    
    return result;
}

// ============================================
// Apply Flipbook to Color
// ============================================

void applyFlipbook(
    inout vec3 baseColor,
    inout float alpha,
    inout vec3 emission,
    sampler2D flipbookTex,
    sampler2D flipbookMask,
    FlipbookData flipbook,
    vec2 uv[4],
    float time
) {
    if (!flipbook.enabled) return;
    
    // Sample flipbook
    vec4 flipbookColor = sampleFlipbook(flipbookTex, flipbook, uv[flipbook.uvChannel], time);
    
    // Apply alpha cutoff
    if (flipbook.useAlpha && flipbookColor.a < flipbook.alphaCutoff) {
        return;
    }
    
    // Get mask
    float mask = 1.0;
    if (flipbook.useMask) {
        mask = texture(flipbookMask, uv[0])[flipbook.maskChannel];
    }
    
    float effectiveIntensity = flipbook.intensity * mask;
    
    // Apply to base color
    baseColor = blendFlipbook(baseColor, flipbookColor.rgb, flipbook.blendMode, 
                              effectiveIntensity, flipbookColor.a);
    
    // Apply alpha blending
    if (flipbook.useAlpha) {
        switch (flipbook.alphaBlendMode) {
            case 0: // Replace
                alpha = mix(alpha, flipbookColor.a, effectiveIntensity);
                break;
            case 1: // Multiply
                alpha *= mix(1.0, flipbookColor.a, effectiveIntensity);
                break;
            case 2: // Add
                alpha = clamp(alpha + flipbookColor.a * effectiveIntensity, 0.0, 1.0);
                break;
        }
    }
    
    // Add emission
    emission += flipbookColor.rgb * flipbookColor.a * flipbook.emission * effectiveIntensity;
}

// ============================================
// Simple Flipbook (single function call)
// ============================================

vec4 simpleFlipbook(
    sampler2D tex,
    vec2 uv,
    int columns,
    int rows,
    float fps,
    float time
) {
    int totalFrames = columns * rows;
    int frame = int(floor(time * fps)) % totalFrames;
    
    vec2 frameUV = getFlipbookFrameUV(uv, frame, columns, rows);
    return texture(tex, frameUV);
}

#endif // TOON_FLIPBOOK_GLSL
