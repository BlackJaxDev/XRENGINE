// ===========================================
// Uber Shader - Decals System
// ===========================================
// Supports 4 decal slots with:
// - UV positioning (position, rotation, scale, side offset)
// - Symmetry and mirroring modes
// - 10 blend modes (Replace, Darken, Multiply, Lighten, Screen, Subtract, Add, Overlay, Mixed, SoftLight)
// - Alpha blending modes
// - Hue shift with HSV/OKLab color spaces
// - Chromatic aberration effect
// - Per-decal masks
// - Emission contribution
// ===========================================

#ifndef TOON_DECALS_GLSL
#define TOON_DECALS_GLSL

// Decal Blend Modes
#define DECAL_BLEND_REPLACE     0
#define DECAL_BLEND_DARKEN      1
#define DECAL_BLEND_MULTIPLY    2
#define DECAL_BLEND_LIGHTEN     3
#define DECAL_BLEND_SCREEN      4
#define DECAL_BLEND_SUBTRACT    5
#define DECAL_BLEND_ADD         6
#define DECAL_BLEND_OVERLAY     7
#define DECAL_BLEND_MIXED       8
#define DECAL_BLEND_SOFTLIGHT   9

// Alpha Override Modes
#define ALPHA_OVERRIDE_OFF      0
#define ALPHA_OVERRIDE_REPLACE  1
#define ALPHA_OVERRIDE_MULTIPLY 2
#define ALPHA_OVERRIDE_ADD      3
#define ALPHA_OVERRIDE_SUBTRACT 4
#define ALPHA_OVERRIDE_MIN      5
#define ALPHA_OVERRIDE_MAX      6

// Symmetry Modes
#define SYMMETRY_NONE           0
#define SYMMETRY_COPY           1  // Mirror copy (abs)
#define SYMMETRY_FLIP           2  // Mirror flip

// Mirrored UV Modes
#define MIRROR_OFF              0
#define MIRROR_FLIP             1  // Flip on right hand
#define MIRROR_HIDE_RIGHT       2  // Hide on right hand
#define MIRROR_HIDE_LEFT        3  // Hide on left hand
#define MIRROR_FLIP_HIDE_LEFT   4  // Flip on right, hide on left

// ============================================
// Decal Data Structure
// ============================================
struct DecalData {
    bool enabled;
    vec2 position;
    float rotation;
    float rotationSpeed;
    vec2 scale;
    vec4 sideOffset;  // x=left, y=right, z=bottom, w=top
    vec4 color;
    float emissionStrength;
    float blendAlpha;
    int blendMode;
    int alphaBlendMode;
    bool tiled;
    float depth;
    int maskChannel;
    
    // Hue Shift
    bool hueShiftEnabled;
    float hueShift;
    float hueShiftSpeed;
    int hueShiftColorSpace;  // 0=HSV, 1=OKLab
    float hueAngleStrength;
    
    // Chromatic Aberration
    bool chromaEnabled;
    float chromaSeparation;
    bool chromaPremultiply;
    float chromaHue;
    float chromaVertical;
    float chromaAngleStrength;
    
    // Alpha Override
    int alphaOverrideMode;
    float alphaOverride;
    
    // Symmetry & Mirroring
    int symmetryMode;
    int mirroredUVMode;
    
    // UV settings
    int uvChannel;
    vec4 textureST;
    vec2 texturePan;
};

// ============================================
// Utility Functions
// ============================================

// Remap value from one range to another
vec2 remapDecal(vec2 value, vec2 inMin, vec2 inMax, vec2 outMin, vec2 outMax) {
    return outMin + (value - inMin) * (outMax - outMin) / (inMax - inMin);
}

// Apply tiling clipping - returns 0 if UV is outside 0-1 range (when tiling is disabled)
float applyTilingClipping(bool tiled, vec2 uv) {
    if (tiled) return 1.0;
    if (uv.x > 1.0 || uv.y > 1.0 || uv.x < 0.0 || uv.y < 0.0) {
        return 0.0;
    }
    return 1.0;
}

// HUE to RGB conversion for chromatic aberration
vec3 HUEtoRGB(float h) {
    float r = abs(h * 6.0 - 3.0) - 1.0;
    float g = 2.0 - abs(h * 6.0 - 2.0);
    float b = 2.0 - abs(h * 6.0 - 4.0);
    return clamp(vec3(r, g, b), 0.0, 1.0);
}

// Hue shift in HSV color space
vec3 hueShiftHSV(vec3 color, float shift) {
    // RGB to HSV
    float maxC = max(max(color.r, color.g), color.b);
    float minC = min(min(color.r, color.g), color.b);
    float delta = maxC - minC;
    
    float h = 0.0;
    if (delta > 0.0001) {
        if (maxC == color.r) {
            h = mod((color.g - color.b) / delta, 6.0);
        } else if (maxC == color.g) {
            h = (color.b - color.r) / delta + 2.0;
        } else {
            h = (color.r - color.g) / delta + 4.0;
        }
        h /= 6.0;
    }
    float s = maxC > 0.0 ? delta / maxC : 0.0;
    float v = maxC;
    
    // Shift hue
    h = fract(h + shift);
    
    // HSV to RGB
    float c = v * s;
    float x = c * (1.0 - abs(mod(h * 6.0, 2.0) - 1.0));
    float m = v - c;
    
    vec3 rgb;
    if (h < 1.0/6.0) rgb = vec3(c, x, 0.0);
    else if (h < 2.0/6.0) rgb = vec3(x, c, 0.0);
    else if (h < 3.0/6.0) rgb = vec3(0.0, c, x);
    else if (h < 4.0/6.0) rgb = vec3(0.0, x, c);
    else if (h < 5.0/6.0) rgb = vec3(x, 0.0, c);
    else rgb = vec3(c, 0.0, x);
    
    return rgb + m;
}

// Apply hue shift to decal color
vec3 decalHueShift(bool enabled, vec3 color, float shift, float shiftSpeed, int colorSpace, float time) {
    if (!enabled) return color;
    
    float totalShift = shift + time * shiftSpeed;
    
    if (colorSpace == 0) {
        // HSV
        return hueShiftHSV(color, totalShift);
    } else {
        // OKLab (simplified - using HSV for now, proper OKLab would be more complex)
        return hueShiftHSV(color, totalShift);
    }
}

// ============================================
// Decal UV Calculation
// ============================================
vec2 calculateDecalUV(
    int uvChannel,
    vec2 position,
    float rotation,
    float rotationSpeed,
    vec2 scale,
    vec4 sideOffset,
    float depth,
    int symmetryMode,
    int mirroredUVMode,
    ToonMesh mesh,
    float time
) {
    // Adjust side offset (negate x and z for correct positioning)
    vec4 adjustedOffset = vec4(-sideOffset.x, sideOffset.y, -sideOffset.z, sideOffset.w);
    vec2 centerOffset = vec2(
        (adjustedOffset.x + adjustedOffset.y) * 0.5,
        (adjustedOffset.z + adjustedOffset.w) * 0.5
    );
    
    // Get base UV
    vec2 uv = mesh.uv[uvChannel];
    
    // Apply symmetry
    if (symmetryMode == SYMMETRY_COPY) {
        // Copy: mirror around center
        uv.x = abs(uv.x - 0.5) + 0.5;
    } else if (symmetryMode == SYMMETRY_FLIP && uv.x < 0.5) {
        // Flip: flip left side
        uv.x = 1.0 - uv.x;
    }
    
    // Determine if "right hand" (uv.x > 0.5)
    bool isRightHand = uv.x > 0.5;
    
    // Apply mirrored UV mode
    if ((mirroredUVMode == MIRROR_FLIP || mirroredUVMode == MIRROR_FLIP_HIDE_LEFT) && isRightHand) {
        uv.x = 1.0 - uv.x;
    }
    if (mirroredUVMode == MIRROR_HIDE_RIGHT && isRightHand) {
        uv.x = -1.0;  // Will be clipped
    }
    if ((mirroredUVMode == MIRROR_HIDE_LEFT || mirroredUVMode == MIRROR_FLIP_HIDE_LEFT) && !isRightHand) {
        uv.x = -1.0;  // Will be clipped
    }
    
    // Calculate rotation
    vec2 decalCenter = position + centerOffset;
    float theta = radians(rotation + time * rotationSpeed);
    float cs = cos(theta);
    float sn = sin(theta);
    
    // Rotate UV around decal center
    vec2 rotatedUV;
    rotatedUV.x = (uv.x - decalCenter.x) * cs - (uv.y - decalCenter.y) * sn + decalCenter.x;
    rotatedUV.y = (uv.x - decalCenter.x) * sn + (uv.y - decalCenter.y) * cs + decalCenter.y;
    
    // Remap to decal space
    vec2 minBound = -scale * 0.5 + position + adjustedOffset.xz;
    vec2 maxBound = scale * 0.5 + position + adjustedOffset.yw;
    uv = remapDecal(rotatedUV, minBound, maxBound, vec2(0.0), vec2(1.0));
    
    return uv;
}

// ============================================
// Blend Mode Functions
// ============================================
vec3 blendDecalColor(vec3 base, vec3 decal, int blendMode) {
    switch (blendMode) {
        case DECAL_BLEND_REPLACE:
            return decal;
        case DECAL_BLEND_DARKEN:
            return min(base, decal);
        case DECAL_BLEND_MULTIPLY:
            return base * decal;
        case DECAL_BLEND_LIGHTEN:
            return max(base, decal);
        case DECAL_BLEND_SCREEN:
            return 1.0 - (1.0 - base) * (1.0 - decal);
        case DECAL_BLEND_SUBTRACT:
            return max(base - decal, vec3(0.0));
        case DECAL_BLEND_ADD:
            return base + decal;
        case DECAL_BLEND_OVERLAY:
            return vec3(
                base.r < 0.5 ? 2.0 * base.r * decal.r : 1.0 - 2.0 * (1.0 - base.r) * (1.0 - decal.r),
                base.g < 0.5 ? 2.0 * base.g * decal.g : 1.0 - 2.0 * (1.0 - base.g) * (1.0 - decal.g),
                base.b < 0.5 ? 2.0 * base.b * decal.b : 1.0 - 2.0 * (1.0 - base.b) * (1.0 - decal.b)
            );
        case DECAL_BLEND_MIXED:
            return base + base * decal;
        case DECAL_BLEND_SOFTLIGHT:
            return vec3(
                decal.r < 0.5 ? base.r - (1.0 - 2.0 * decal.r) * base.r * (1.0 - base.r) 
                              : base.r + (2.0 * decal.r - 1.0) * (sqrt(base.r) - base.r),
                decal.g < 0.5 ? base.g - (1.0 - 2.0 * decal.g) * base.g * (1.0 - base.g) 
                              : base.g + (2.0 * decal.g - 1.0) * (sqrt(base.g) - base.g),
                decal.b < 0.5 ? base.b - (1.0 - 2.0 * decal.b) * base.b * (1.0 - base.b) 
                              : base.b + (2.0 * decal.b - 1.0) * (sqrt(base.b) - base.b)
            );
        default:
            return decal;
    }
}

// Apply alpha override to fragment alpha
void applyDecalAlphaOverride(inout float fragAlpha, float decalAlpha, int overrideMode) {
    switch (overrideMode) {
        case ALPHA_OVERRIDE_REPLACE:
            fragAlpha = decalAlpha;
            break;
        case ALPHA_OVERRIDE_MULTIPLY:
            fragAlpha = clamp(fragAlpha * decalAlpha, 0.0, 1.0);
            break;
        case ALPHA_OVERRIDE_ADD:
            fragAlpha = clamp(fragAlpha + decalAlpha, 0.0, 1.0);
            break;
        case ALPHA_OVERRIDE_SUBTRACT:
            fragAlpha = clamp(fragAlpha - decalAlpha, 0.0, 1.0);
            break;
        case ALPHA_OVERRIDE_MIN:
            fragAlpha = min(fragAlpha, decalAlpha);
            break;
        case ALPHA_OVERRIDE_MAX:
            fragAlpha = max(fragAlpha, decalAlpha);
            break;
    }
}

// ============================================
// Decal Sampling Functions
// ============================================

// Sample a single decal
vec4 sampleDecal(
    sampler2D decalTex,
    DecalData decal,
    ToonMesh mesh,
    ToonLight light,
    vec4 decalMask,
    float time
) {
    if (!decal.enabled) return vec4(0.0);
    
    // Calculate UV
    vec2 uv = calculateDecalUV(
        decal.uvChannel,
        decal.position,
        decal.rotation,
        decal.rotationSpeed,
        decal.scale,
        decal.sideOffset,
        decal.depth,
        decal.symmetryMode,
        decal.mirroredUVMode,
        mesh,
        time
    );
    
    // Check tiling clipping
    float clip = applyTilingClipping(decal.tiled, uv);
    if (clip < 0.5) return vec4(0.0);
    
    // Transform UV with ST
    vec2 texUV = uv * decal.textureST.xy + decal.textureST.zw + decal.texturePan * time;
    
    // Sample texture
    vec4 decalColor = texture(decalTex, texUV) * decal.color;
    
    // Apply hue shift
    float hueOffset = decal.hueAngleStrength * light.nDotV;
    decalColor.rgb = decalHueShift(
        decal.hueShiftEnabled,
        decalColor.rgb,
        decal.hueShift + hueOffset,
        decal.hueShiftSpeed,
        decal.hueShiftColorSpace,
        time
    );
    
    // Apply mask
    decalColor.a *= decalMask[decal.maskChannel] * clip;
    
    return decalColor;
}

// Sample decal with chromatic aberration
vec4 sampleDecalChromatic(
    sampler2D decalTex,
    DecalData decal,
    ToonMesh mesh,
    ToonLight light,
    vec4 decalMask,
    float time
) {
    if (!decal.enabled || !decal.chromaEnabled) {
        return sampleDecal(decalTex, decal, mesh, light, decalMask, time);
    }
    
    // Calculate chromatic offset based on angle
    float chromaOffset = decal.chromaSeparation + 
        decal.chromaAngleStrength * (decal.chromaAngleStrength > 0.0 ? (1.0 - light.nDotV) : light.nDotV);
    
    vec2 positionOffset = chromaOffset * 0.01 * (decal.scale.x + decal.scale.y) * 
        vec2(cos(decal.chromaVertical), sin(decal.chromaVertical));
    
    // Sample with offset positions
    DecalData decal0 = decal;
    DecalData decal1 = decal;
    decal0.position += positionOffset;
    decal1.position -= positionOffset;
    
    vec2 uv0 = calculateDecalUV(
        decal.uvChannel, decal0.position, decal.rotation, decal.rotationSpeed,
        decal.scale, decal.sideOffset, decal.depth, decal.symmetryMode,
        decal.mirroredUVMode, mesh, time
    );
    
    vec2 uv1 = calculateDecalUV(
        decal.uvChannel, decal1.position, decal.rotation, decal.rotationSpeed,
        decal.scale, decal.sideOffset, decal.depth, decal.symmetryMode,
        decal.mirroredUVMode, mesh, time
    );
    
    vec2 texUV0 = uv0 * decal.textureST.xy + decal.textureST.zw + decal.texturePan * time;
    vec2 texUV1 = uv1 * decal.textureST.xy + decal.textureST.zw + decal.texturePan * time;
    
    vec4 sample0 = texture(decalTex, texUV0) * decal.color;
    vec4 sample1 = texture(decalTex, texUV1) * decal.color;
    
    // Apply hue shift to both samples
    float hueOffset = decal.hueAngleStrength * light.nDotV;
    sample0.rgb = decalHueShift(decal.hueShiftEnabled, sample0.rgb, 
        decal.hueShift + hueOffset, decal.hueShiftSpeed, decal.hueShiftColorSpace, time);
    sample1.rgb = decalHueShift(decal.hueShiftEnabled, sample1.rgb, 
        decal.hueShift + hueOffset, decal.hueShiftSpeed, decal.hueShiftColorSpace, time);
    
    // Blend with chromatic color
    vec3 chromaColor = HUEtoRGB(fract(decal.chromaHue));
    
    vec4 result;
    if (decal.chromaPremultiply) {
        result.rgb = mix(sample0.rgb * sample0.a, sample1.rgb * sample1.a, chromaColor);
    } else {
        result.rgb = mix(sample0.rgb, sample1.rgb, chromaColor);
    }
    result.a = 0.5 * (sample0.a + sample1.a);
    
    float clip = max(applyTilingClipping(decal.tiled, uv0), applyTilingClipping(decal.tiled, uv1));
    result.a *= decalMask[decal.maskChannel] * clip;
    
    return result;
}

// ============================================
// Apply Decal to Base Color
// ============================================
void applyDecal(
    inout vec3 baseColor,
    inout float fragAlpha,
    inout vec3 emission,
    vec4 decalColor,
    DecalData decal,
    ToonMesh mesh
) {
    if (!decal.enabled || decalColor.a < 0.001) return;
    
    // Determine if on right/left side for hide modes
    bool isRightHand = mesh.uv[0].x > 0.5;
    
    // Check mirrored UV hide modes
    if (decal.mirroredUVMode == MIRROR_HIDE_RIGHT && isRightHand) return;
    if ((decal.mirroredUVMode == MIRROR_HIDE_LEFT || decal.mirroredUVMode == MIRROR_FLIP_HIDE_LEFT) && !isRightHand) return;
    
    // Calculate final alpha
    float decalAlphaMixed = decalColor.a * clamp(decal.blendAlpha, 0.0, 1.0);
    
    // Apply alpha override
    if (decal.alphaOverrideMode != ALPHA_OVERRIDE_OFF) {
        applyDecalAlphaOverride(fragAlpha, decalAlphaMixed, decal.alphaOverrideMode);
    }
    
    // Blend color
    baseColor = mix(baseColor, blendDecalColor(baseColor, decalColor.rgb, decal.blendMode), decalAlphaMixed);
    
    // Add emission
    emission += decalColor.rgb * decalColor.a * max(decal.emissionStrength, 0.0);
}

#endif // TOON_DECALS_GLSL
