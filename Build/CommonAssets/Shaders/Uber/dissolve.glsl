// Dissolve Effect Module

#ifndef TOON_DISSOLVE_GLSL
#define TOON_DISSOLVE_GLSL

// ============================================
// Dissolve Uniforms (defined in uniforms.glsl)
// ============================================
// uniform float _EnableDissolve;
// uniform float _DissolveType;           // 0=Basic, 1=Point2Point, 2=Spherical
// uniform float _DissolveProgress;       // 0-1 dissolve amount
// uniform vec4 _DissolveEdgeColor;
// uniform float _DissolveEdgeWidth;
// uniform float _DissolveEdgeEmission;
// uniform sampler2D _DissolveNoiseTexture;
// uniform vec4 _DissolveNoiseTexture_ST;
// uniform float _DissolveNoiseStrength;
// uniform vec3 _DissolveStartPoint;      // For point-to-point
// uniform vec3 _DissolveEndPoint;
// uniform float _DissolveInvert;
// uniform float _DissolveCutoff;         // Alpha cutoff threshold

// Dissolve type constants
const int DISSOLVE_BASIC = 0;
const int DISSOLVE_POINT2POINT = 1;
const int DISSOLVE_SPHERICAL = 2;

// ============================================
// Dissolve Noise Sampling
// ============================================
float sampleDissolveNoise(vec2 uv, vec4 textureST) {
    vec2 noiseUV = uv * textureST.xy + textureST.zw;
    return texture(_DissolveNoiseTexture, noiseUV).r;
}

// ============================================
// Point-to-Point Dissolve
// ============================================
float calculatePoint2PointDissolve(vec3 worldPos, vec3 startPoint, vec3 endPoint, float progress) {
    vec3 direction = endPoint - startPoint;
    float totalDist = length(direction);
    
    if (totalDist < 0.001) {
        return progress;
    }
    
    direction = normalize(direction);
    vec3 toPos = worldPos - startPoint;
    float projDist = dot(toPos, direction);
    
    // Normalize to 0-1 range
    float normalizedDist = projDist / totalDist;
    
    return normalizedDist;
}

// ============================================
// Spherical Dissolve
// ============================================
float calculateSphericalDissolve(vec3 worldPos, vec3 centerPoint, float maxRadius, float progress) {
    float dist = length(worldPos - centerPoint);
    float normalizedDist = dist / max(maxRadius, 0.001);
    
    return normalizedDist;
}

// ============================================
// Calculate Dissolve Value
// ============================================
// Returns: x = dissolve alpha (0 = dissolved, 1 = solid)
//          y = edge factor (0-1, 1 at edge)
//          z = raw dissolve value
vec3 calculateDissolve(vec2 uv, vec3 worldPos, vec3 localPos) {
    if (_EnableDissolve < 0.5) {
        return vec3(1.0, 0.0, 0.0);
    }
    
    float dissolveValue = 0.0;
    int dissolveType = int(_DissolveType);
    
    // Calculate base dissolve value based on type
    if (dissolveType == DISSOLVE_BASIC) {
        // Basic UV-based dissolve with noise
        dissolveValue = sampleDissolveNoise(uv, _DissolveNoiseTexture_ST);
    }
    else if (dissolveType == DISSOLVE_POINT2POINT) {
        // Point-to-point dissolve
        float p2p = calculatePoint2PointDissolve(worldPos, _DissolveStartPoint, _DissolveEndPoint, _DissolveProgress);
        float noise = sampleDissolveNoise(uv, _DissolveNoiseTexture_ST);
        dissolveValue = mix(p2p, p2p + (noise - 0.5) * 2.0, _DissolveNoiseStrength);
    }
    else if (dissolveType == DISSOLVE_SPHERICAL) {
        // Spherical dissolve from start point
        float maxRadius = length(_DissolveEndPoint - _DissolveStartPoint);
        float spherical = calculateSphericalDissolve(worldPos, _DissolveStartPoint, maxRadius, _DissolveProgress);
        float noise = sampleDissolveNoise(uv, _DissolveNoiseTexture_ST);
        dissolveValue = mix(spherical, spherical + (noise - 0.5) * 2.0, _DissolveNoiseStrength);
    }
    
    // Invert if needed
    if (_DissolveInvert > 0.5) {
        dissolveValue = 1.0 - dissolveValue;
    }
    
    // Calculate edge
    float progress = _DissolveProgress;
    float edgeWidth = _DissolveEdgeWidth;
    
    // Dissolve threshold
    float threshold = progress;
    float edgeThreshold = threshold + edgeWidth;
    
    // Calculate alpha (1 = visible, 0 = dissolved)
    float alpha = step(threshold, dissolveValue);
    
    // Calculate edge factor (smooth gradient at edge)
    float edgeFactor = 0.0;
    if (dissolveValue >= threshold && dissolveValue < edgeThreshold && edgeWidth > 0.001) {
        edgeFactor = 1.0 - (dissolveValue - threshold) / edgeWidth;
    }
    
    return vec3(alpha, edgeFactor, dissolveValue);
}

// ============================================
// Apply Dissolve to Fragment
// ============================================
// Modifies color and emission, returns true if fragment should be discarded
bool applyDissolve(vec2 uv, vec3 worldPos, vec3 localPos, inout vec3 color, inout vec3 emission, inout float alpha) {
    if (_EnableDissolve < 0.5) {
        return false;
    }
    
    vec3 dissolveResult = calculateDissolve(uv, worldPos, localPos);
    float dissolveAlpha = dissolveResult.x;
    float edgeFactor = dissolveResult.y;
    
    // Discard fully dissolved pixels
    if (dissolveAlpha < _DissolveCutoff) {
        return true; // Should discard
    }
    
    // Apply edge color and emission
    if (edgeFactor > 0.0) {
        // Blend edge color
        color = mix(color, _DissolveEdgeColor.rgb, edgeFactor * _DissolveEdgeColor.a);
        
        // Add edge emission
        emission += _DissolveEdgeColor.rgb * edgeFactor * _DissolveEdgeEmission;
    }
    
    return false;
}

// ============================================
// Gradient Dissolve (Alternative Mode)
// ============================================
float calculateGradientDissolve(vec3 localPos, vec3 direction, float progress) {
    // Project local position onto direction
    float projected = dot(localPos, normalize(direction));
    
    // Remap to 0-1 based on object bounds (assumes -0.5 to 0.5 range)
    float normalized = projected + 0.5;
    
    return normalized;
}

// ============================================
// Texture-Based Dissolve Pattern
// ============================================
float calculateTextureDissolve(vec2 uv, float progress, float noiseStrength) {
    float noise = sampleDissolveNoise(uv, _DissolveNoiseTexture_ST);
    
    // Apply noise strength
    float dissolveValue = mix(0.5, noise, noiseStrength);
    
    return dissolveValue;
}

// ============================================
// Edge Glow Effect (for emission)
// ============================================
vec3 calculateDissolveEdgeGlow(float edgeFactor, vec3 edgeColor, float glowWidth, float glowIntensity) {
    // Create a soft glow that extends beyond the hard edge
    float glow = smoothstep(0.0, glowWidth, edgeFactor);
    glow = pow(glow, 2.0); // Falloff curve
    
    return edgeColor * glow * glowIntensity;
}

// ============================================
// Burn Effect (Color gradient along edge)
// ============================================
vec3 calculateBurnEffect(float edgeFactor, vec3 innerColor, vec3 outerColor) {
    // Gradient from outer (yellow/white) to inner (red/orange) along edge
    return mix(outerColor, innerColor, edgeFactor);
}

#endif // TOON_DISSOLVE_GLSL
