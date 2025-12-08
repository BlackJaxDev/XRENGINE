// ===========================================
// Uber Shader - Glitter / Sparkle
// ===========================================
// Procedural sparkle/glitter effect
// ===========================================

#ifndef TOON_GLITTER_GLSL
#define TOON_GLITTER_GLSL

// Glitter Modes
#define GLITTER_MODE_RANDOM         0   // Random sparkles
#define GLITTER_MODE_LINEAR         1   // Linear pattern
#define GLITTER_MODE_ANGLE          2   // View-angle based

// Glitter Shapes
#define GLITTER_SHAPE_CIRCLE        0
#define GLITTER_SHAPE_SQUARE        1
#define GLITTER_SHAPE_DIAMOND       2
#define GLITTER_SHAPE_STAR          3

// ============================================
// Glitter Data Structure
// ============================================
struct GlitterData {
    bool enabled;
    vec3 color;
    float colorMix;
    
    // Density & Size
    float density;              // How many sparkles per area
    float size;                 // Size of each sparkle
    float sizeVariation;        // Random size variation
    
    // Behavior
    int mode;
    int shape;
    float speed;                // Animation speed
    float contrast;             // Sparkle sharpness
    float brightness;
    float emission;
    
    // Angle-based
    float angleSensitivity;     // How much view angle affects sparkle
    float angleOffset;
    
    // Random
    float randomSeed;
    float randomIntensity;      // Random brightness variation
    
    // Mask & Hide
    float hideInShadow;         // Reduce in shadow
    int maskChannel;
    bool useMask;
    
    // UV Settings
    int uvChannel;
    float uvScale;
    
    // Jitter
    float jitterSpeed;
    float jitterIntensity;
};

// ============================================
// Hash Functions for Randomness
// ============================================

float hash11(float p) {
    p = fract(p * 0.1031);
    p *= p + 33.33;
    p *= p + p;
    return fract(p);
}

float hash21(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

vec2 hash22(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.xx + p3.yz) * p3.zy);
}

vec3 hash32(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yxz + 33.33);
    return fract((p3.xxy + p3.yzz) * p3.zyx);
}

// ============================================
// Voronoi-based Sparkle Pattern
// ============================================

vec3 voronoiSparkle(vec2 uv, float density, float seed) {
    vec2 id = floor(uv * density);
    vec2 gv = fract(uv * density);
    
    float minDist = 10.0;
    vec2 closestPoint = vec2(0.0);
    vec2 closestId = vec2(0.0);
    
    // Check 3x3 neighborhood
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            vec2 offset = vec2(float(x), float(y));
            vec2 cellId = id + offset;
            
            // Random point within cell
            vec2 rand = hash22(cellId + seed);
            vec2 point = offset + rand - gv;
            
            float dist = length(point);
            if (dist < minDist) {
                minDist = dist;
                closestPoint = point;
                closestId = cellId;
            }
        }
    }
    
    return vec3(minDist, closestId);
}

// ============================================
// Shape Functions
// ============================================

float glitterShapeCircle(vec2 p, float size) {
    return 1.0 - smoothstep(0.0, size, length(p));
}

float glitterShapeSquare(vec2 p, float size) {
    vec2 d = abs(p) - vec2(size);
    return 1.0 - smoothstep(0.0, size * 0.1, max(d.x, d.y));
}

float glitterShapeDiamond(vec2 p, float size) {
    return 1.0 - smoothstep(0.0, size, abs(p.x) + abs(p.y));
}

float glitterShapeStar(vec2 p, float size) {
    float a = atan(p.y, p.x);
    float r = length(p);
    float star = abs(sin(a * 2.5)) * 0.5 + 0.5;
    return 1.0 - smoothstep(0.0, size, r / star);
}

float getGlitterShape(vec2 p, int shape, float size) {
    switch (shape) {
        case GLITTER_SHAPE_CIRCLE:
            return glitterShapeCircle(p, size);
        case GLITTER_SHAPE_SQUARE:
            return glitterShapeSquare(p, size);
        case GLITTER_SHAPE_DIAMOND:
            return glitterShapeDiamond(p, size);
        case GLITTER_SHAPE_STAR:
            return glitterShapeStar(p, size);
        default:
            return glitterShapeCircle(p, size);
    }
}

// ============================================
// Calculate Glitter
// ============================================

vec4 calculateGlitter(
    GlitterData glitter,
    vec2 uv,
    vec3 normal,
    vec3 viewDir,
    float shadow,
    float time
) {
    if (!glitter.enabled) {
        return vec4(0.0);
    }
    
    // Scale UV
    vec2 scaledUV = uv * glitter.uvScale;
    
    // Add jitter animation
    if (glitter.jitterSpeed > 0.0) {
        vec2 jitter = hash22(floor(scaledUV * glitter.density)) - 0.5;
        scaledUV += jitter * glitter.jitterIntensity * sin(time * glitter.jitterSpeed);
    }
    
    // Get voronoi pattern
    vec3 voronoi = voronoiSparkle(scaledUV, glitter.density, glitter.randomSeed);
    float dist = voronoi.x;
    vec2 cellId = voronoi.yz;
    
    // Get random values for this sparkle
    vec3 rand = hash32(cellId + glitter.randomSeed);
    float sizeRand = mix(1.0, rand.x, glitter.sizeVariation);
    float brightRand = mix(1.0, rand.y, glitter.randomIntensity);
    float timeOffset = rand.z * 6.28318;
    
    // Calculate sparkle size
    float sparkleSize = glitter.size * 0.1 * sizeRand;
    
    // Mode-based intensity
    float modeIntensity = 1.0;
    
    switch (glitter.mode) {
        case GLITTER_MODE_RANDOM:
            // Animate with time
            float flicker = sin(time * glitter.speed + timeOffset) * 0.5 + 0.5;
            modeIntensity = pow(flicker, glitter.contrast);
            break;
            
        case GLITTER_MODE_LINEAR:
            // Linear sweep
            float sweep = fract(scaledUV.x + scaledUV.y + time * glitter.speed * 0.1);
            modeIntensity = pow(sweep, glitter.contrast);
            break;
            
        case GLITTER_MODE_ANGLE:
            // View angle based
            float fresnel = 1.0 - abs(dot(normal, viewDir));
            float anglePhase = fresnel * glitter.angleSensitivity + glitter.angleOffset;
            modeIntensity = pow(abs(sin(anglePhase * 3.14159 + rand.z * 6.28318)), glitter.contrast);
            break;
    }
    
    // Get shape
    vec2 cellUV = fract(scaledUV * glitter.density) - 0.5;
    float shape = getGlitterShape(cellUV, glitter.shape, sparkleSize);
    
    // Combine
    float intensity = shape * modeIntensity * brightRand * glitter.brightness;
    
    // Shadow hiding
    intensity *= mix(1.0, shadow, glitter.hideInShadow);
    
    // Apply contrast
    intensity = pow(clamp(intensity, 0.0, 1.0), max(glitter.contrast, 0.1));
    
    return vec4(glitter.color * intensity, intensity);
}

// ============================================
// Apply Glitter to Color
// ============================================

void applyGlitter(
    inout vec3 baseColor,
    inout vec3 emission,
    GlitterData glitter,
    sampler2D glitterMask,
    vec2 uv[4],
    vec3 normal,
    vec3 viewDir,
    float shadow,
    float time
) {
    if (!glitter.enabled) return;
    
    vec2 glitterUV = uv[glitter.uvChannel];
    
    // Calculate glitter
    vec4 glitterResult = calculateGlitter(glitter, glitterUV, normal, viewDir, shadow, time);
    
    // Apply mask
    float mask = 1.0;
    if (glitter.useMask) {
        mask = texture(glitterMask, uv[0])[glitter.maskChannel];
    }
    
    float glitterIntensity = glitterResult.a * mask;
    vec3 glitterColor = glitterResult.rgb;
    
    // Mix with base color
    baseColor = mix(baseColor, mix(baseColor, glitterColor, glitter.colorMix), glitterIntensity);
    
    // Add emission
    emission += glitterColor * glitterIntensity * glitter.emission;
}

#endif // TOON_GLITTER_GLSL
