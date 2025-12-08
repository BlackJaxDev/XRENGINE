// ===========================================
// Uber Shader - Emission Module
// ===========================================
// Supports multiple emission slots (up to 4) with:
// - Per-slot maps and colors
// - Scrolling/wave effects
// - Light-based emission (GITD - Glow in the Dark)
// - Center-out effects
// - Blinking/pulsing
// - Audio reactivity placeholder
// ===========================================

#ifndef TOON_EMISSION_GLSL
#define TOON_EMISSION_GLSL

// ============================================
// Emission Data Structures
// ============================================
struct EmissionScrolling {
    bool enabled;
    bool useVertexColors;
    vec2 direction;         // Scroll direction (x, y)
    float width;            // Wave width
    float velocity;         // Scroll speed
    float interval;         // Gap between waves
    float offset;           // Phase offset
    int curveType;          // 0=Linear, 1=Exponential, 2=Sine
};

struct EmissionBlinking {
    bool enabled;
    float min;              // Minimum brightness
    float max;              // Maximum brightness
    float velocity;         // Blink speed
    float offset;           // Phase offset
};

struct EmissionCenterOut {
    bool enabled;
    float speed;
    float size;             // Intensity threshold
    float duration;         // History length
    int band;               // Audio band (for AudioLink)
};

struct EmissionLightBased {
    bool enabled;
    float threshold;        // Light level threshold
    float strength;         // Emission strength multiplier
    bool invert;            // Invert (glow when lit vs dark)
    float minBrightness;    // Minimum ambient for effect
    float maxBrightness;    // Maximum ambient for effect
};

struct EmissionSlot {
    bool enabled;
    vec4 color;             // Emission color with alpha
    float strength;         // Emission strength multiplier
    int uvChannel;          // UV channel to use
    vec4 textureST;         // Texture scale/offset
    vec2 texturePan;        // Texture panning
    
    // Mask settings
    int maskChannel;        // RGBA channel of mask texture
    bool invertMask;
    float maskStrength;     // Blend with mask
    
    // Effects
    EmissionScrolling scrolling;
    EmissionBlinking blinking;
    EmissionCenterOut centerOut;
    EmissionLightBased lightBased;
    
    // Hue shift
    bool hueShiftEnabled;
    float hueShift;
    float hueShiftSpeed;
};

// ============================================
// Emission Calculation Functions
// ============================================

// Scrolling emission wave
float calculateScrollingEmission(EmissionScrolling scroll, vec2 uv, vec4 vertexColor, float time) {
    if (!scroll.enabled) return 1.0;
    
    // Use vertex colors for direction if enabled
    vec2 dir = scroll.useVertexColors ? vertexColor.rg * 2.0 - 1.0 : scroll.direction;
    
    // Calculate wave position
    float pos = dot(uv, dir) * scroll.width;
    float phase = time * scroll.velocity + scroll.offset;
    
    float wave;
    switch (scroll.curveType) {
        case 0: // Linear
            wave = fract(pos - phase);
            break;
        case 1: // Exponential
            wave = fract(pos - phase);
            wave = wave * wave;
            break;
        case 2: // Sine
            wave = sin((pos - phase) * 6.28318) * 0.5 + 0.5;
            break;
        default:
            wave = fract(pos - phase);
    }
    
    // Apply interval (creates gaps)
    if (scroll.interval > 0.0) {
        float intervalMod = mod(pos - phase, scroll.interval + scroll.width);
        wave *= step(intervalMod, scroll.width);
    }
    
    return wave;
}

// Blinking emission
float calculateBlinkingEmission(EmissionBlinking blink, float time) {
    if (!blink.enabled) return 1.0;
    
    float phase = time * blink.velocity + blink.offset;
    float blink_value = sin(phase * 6.28318) * 0.5 + 0.5;
    
    return mix(blink.min, blink.max, blink_value);
}

// Light-based emission (glow in the dark)
float calculateLightBasedEmission(EmissionLightBased gitd, float lightLevel, float ambientLevel) {
    if (!gitd.enabled) return 1.0;
    
    // Clamp ambient to range
    float ambient = clamp(ambientLevel, gitd.minBrightness, gitd.maxBrightness);
    float normalizedAmbient = (ambient - gitd.minBrightness) / max(gitd.maxBrightness - gitd.minBrightness, 0.001);
    
    // Calculate emission based on light threshold
    float emission;
    if (gitd.invert) {
        // Glow when lit
        emission = smoothstep(gitd.threshold - 0.1, gitd.threshold + 0.1, lightLevel);
    } else {
        // Glow when dark
        emission = 1.0 - smoothstep(gitd.threshold - 0.1, gitd.threshold + 0.1, lightLevel);
    }
    
    return emission * gitd.strength * (1.0 - normalizedAmbient);
}

// Center-out wave effect
float calculateCenterOutEmission(EmissionCenterOut centerOut, vec2 uv, float time) {
    if (!centerOut.enabled) return 1.0;
    
    // Distance from center
    float dist = length(uv - 0.5) * 2.0;
    
    // Wave expanding from center
    float phase = fract(time * centerOut.speed);
    float wave = 1.0 - smoothstep(phase - centerOut.size, phase + centerOut.size, dist);
    
    return wave;
}

// Hue shift for emission
vec3 emissionHueShift(vec3 color, float shift, float speed, float time, bool enabled) {
    if (!enabled) return color;
    
    float totalShift = shift + time * speed;
    
    // RGB to HSV
    float maxC = max(max(color.r, color.g), color.b);
    float minC = min(min(color.r, color.g), color.b);
    float delta = maxC - minC;
    
    float h = 0.0;
    if (delta > 0.0001) {
        if (maxC == color.r) h = mod((color.g - color.b) / delta, 6.0);
        else if (maxC == color.g) h = (color.b - color.r) / delta + 2.0;
        else h = (color.r - color.g) / delta + 4.0;
        h /= 6.0;
    }
    float s = maxC > 0.0 ? delta / maxC : 0.0;
    float v = maxC;
    
    // Apply shift
    h = fract(h + totalShift);
    
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

// ============================================
// Single Emission Slot Calculation
// ============================================
vec3 calculateEmissionSlot(
    sampler2D emissionMap,
    sampler2D emissionMask,
    EmissionSlot slot,
    ToonMesh mesh,
    ToonLight light,
    float time
) {
    if (!slot.enabled) return vec3(0.0);
    
    // Get UV
    vec2 uv = mesh.uv[slot.uvChannel];
    vec2 texUV = uv * slot.textureST.xy + slot.textureST.zw + slot.texturePan * time;
    
    // Sample emission map
    vec3 emissionColor = texture(emissionMap, texUV).rgb;
    
    // Apply color tint
    emissionColor *= slot.color.rgb * slot.color.a;
    
    // Sample mask
    float mask = texture(emissionMask, uv)[slot.maskChannel];
    if (slot.invertMask) {
        mask = 1.0 - mask;
    }
    mask = mix(1.0, mask, slot.maskStrength);
    
    // Apply scrolling effect
    float scrollEffect = calculateScrollingEmission(slot.scrolling, uv, mesh.vertexColor, time);
    
    // Apply blinking effect
    float blinkEffect = calculateBlinkingEmission(slot.blinking, time);
    
    // Apply light-based emission
    float lightEffect = calculateLightBasedEmission(
        slot.lightBased,
        light.lightMap,
        dot(light.indirectColor, vec3(0.333))
    );
    
    // Apply center-out effect
    float centerOutEffect = calculateCenterOutEmission(slot.centerOut, uv, time);
    
    // Combine effects
    float combinedEffect = scrollEffect * blinkEffect * lightEffect * centerOutEffect;
    
    // Apply hue shift
    emissionColor = emissionHueShift(
        emissionColor,
        slot.hueShift,
        slot.hueShiftSpeed,
        time,
        slot.hueShiftEnabled
    );
    
    // Final emission
    return emissionColor * slot.strength * mask * combinedEffect;
}

// ============================================
// Apply Multiple Emission Slots
// ============================================
vec3 calculateMultipleEmissions(
    // Slot 0
    sampler2D emissionMap0,
    sampler2D emissionMask0,
    EmissionSlot slot0,
    // Slot 1
    sampler2D emissionMap1,
    sampler2D emissionMask1,
    EmissionSlot slot1,
    // Common parameters
    ToonMesh mesh,
    ToonLight light,
    float time
) {
    vec3 totalEmission = vec3(0.0);
    
    // Calculate each slot
    totalEmission += calculateEmissionSlot(emissionMap0, emissionMask0, slot0, mesh, light, time);
    totalEmission += calculateEmissionSlot(emissionMap1, emissionMask1, slot1, mesh, light, time);
    
    return totalEmission;
}

// ============================================
// Simple Emission (backward compatibility)
// ============================================
vec3 calculateSimpleEmission(
    sampler2D emissionMap,
    vec2 uv,
    vec4 emissionColor,
    float emissionStrength
) {
    vec3 emission = texture(emissionMap, uv).rgb;
    emission *= emissionColor.rgb * emissionColor.a * emissionStrength;
    return emission;
}

#endif // TOON_EMISSION_GLSL
