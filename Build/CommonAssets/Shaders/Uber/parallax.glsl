// Parallax Occlusion Mapping Module

#ifndef TOON_PARALLAX_GLSL
#define TOON_PARALLAX_GLSL

// ============================================
// Parallax Uniforms (defined in uniforms.glsl)
// ============================================
// uniform float _EnableParallax;
// uniform sampler2D _ParallaxMap;        // Height map (usually R channel)
// uniform vec4 _ParallaxMap_ST;
// uniform float _ParallaxStrength;       // Height scale (0.01-0.1 typical)
// uniform float _ParallaxMinSamples;     // Min ray march steps (4-8)
// uniform float _ParallaxMaxSamples;     // Max ray march steps (16-64)
// uniform float _ParallaxOffset;         // Base height offset
// uniform float _ParallaxMapChannel;     // 0=R, 1=G, 2=B, 3=A

// Parallax mode constants
const int PARALLAX_SIMPLE = 0;
const int PARALLAX_STEEP = 1;
const int PARALLAX_OCCLUSION = 2;
const int PARALLAX_RELIEF = 3;

// ============================================
// Sample Height Map
// ============================================
float sampleHeightMap(vec2 uv) {
    vec4 heightSample = texture(_ParallaxMap, uv);
    
    int channel = int(_ParallaxMapChannel);
    float height = 0.0;
    
    if (channel == 0) height = heightSample.r;
    else if (channel == 1) height = heightSample.g;
    else if (channel == 2) height = heightSample.b;
    else height = heightSample.a;
    
    return height;
}

// ============================================
// Simple Parallax Mapping
// ============================================
// Basic offset based on view angle
vec2 calculateSimpleParallax(vec2 uv, vec3 viewDirTangent, float heightScale) {
    float height = sampleHeightMap(uv);
    height = height * heightScale - heightScale * 0.5 + _ParallaxOffset;
    
    vec2 offset = viewDirTangent.xy / viewDirTangent.z * height;
    return uv - offset;
}

// ============================================
// Steep Parallax Mapping
// ============================================
// Linear search through height layers
vec2 calculateSteepParallax(vec2 uv, vec3 viewDirTangent, float heightScale, int numSamples) {
    // Calculate step size
    float layerDepth = 1.0 / float(numSamples);
    float currentLayerDepth = 0.0;
    
    // Calculate UV offset per layer
    vec2 deltaUV = viewDirTangent.xy * heightScale / float(numSamples);
    
    vec2 currentUV = uv;
    float currentHeight = sampleHeightMap(currentUV);
    
    // Linear search
    for (int i = 0; i < numSamples; i++) {
        if (currentLayerDepth >= currentHeight) {
            break;
        }
        
        currentUV -= deltaUV;
        currentHeight = sampleHeightMap(currentUV);
        currentLayerDepth += layerDepth;
    }
    
    return currentUV;
}

// ============================================
// Parallax Occlusion Mapping (POM)
// ============================================
// Steep parallax with binary refinement
vec2 calculateParallaxOcclusion(vec2 uv, vec3 viewDirTangent, float heightScale, int minSamples, int maxSamples) {
    // Calculate number of samples based on view angle
    float viewAngle = abs(dot(vec3(0.0, 0.0, 1.0), viewDirTangent));
    int numSamples = int(mix(float(maxSamples), float(minSamples), viewAngle));
    
    // Calculate step size
    float layerDepth = 1.0 / float(numSamples);
    float currentLayerDepth = 0.0;
    
    // Calculate UV offset per layer
    vec2 deltaUV = viewDirTangent.xy * heightScale / float(numSamples);
    
    vec2 currentUV = uv;
    float currentHeight = sampleHeightMap(currentUV);
    
    // Linear search to find intersection
    vec2 prevUV = currentUV;
    float prevHeight = currentHeight;
    float prevLayerDepth = currentLayerDepth;
    
    for (int i = 0; i < maxSamples; i++) {
        if (currentLayerDepth >= currentHeight) {
            break;
        }
        
        prevUV = currentUV;
        prevHeight = currentHeight;
        prevLayerDepth = currentLayerDepth;
        
        currentUV -= deltaUV;
        currentHeight = sampleHeightMap(currentUV);
        currentLayerDepth += layerDepth;
    }
    
    // Interpolation between previous and current layer
    float afterDepth = currentHeight - currentLayerDepth;
    float beforeDepth = prevHeight - prevLayerDepth;
    float weight = afterDepth / (afterDepth - beforeDepth);
    
    vec2 finalUV = mix(currentUV, prevUV, weight);
    
    return finalUV;
}

// ============================================
// Relief Mapping (Highest Quality)
// ============================================
// POM with binary search refinement
vec2 calculateReliefParallax(vec2 uv, vec3 viewDirTangent, float heightScale, int linearSamples, int binarySamples) {
    // First pass: Linear search
    float layerDepth = 1.0 / float(linearSamples);
    float currentLayerDepth = 0.0;
    
    vec2 deltaUV = viewDirTangent.xy * heightScale / float(linearSamples);
    
    vec2 currentUV = uv;
    float currentHeight = sampleHeightMap(currentUV);
    
    for (int i = 0; i < linearSamples; i++) {
        if (currentLayerDepth >= currentHeight) {
            break;
        }
        
        currentUV -= deltaUV;
        currentHeight = sampleHeightMap(currentUV);
        currentLayerDepth += layerDepth;
    }
    
    // Second pass: Binary search refinement
    deltaUV *= 0.5;
    layerDepth *= 0.5;
    
    for (int i = 0; i < binarySamples; i++) {
        currentHeight = sampleHeightMap(currentUV);
        
        if (currentHeight > currentLayerDepth) {
            currentUV -= deltaUV;
            currentLayerDepth += layerDepth;
        } else {
            currentUV += deltaUV;
            currentLayerDepth -= layerDepth;
        }
        
        deltaUV *= 0.5;
        layerDepth *= 0.5;
    }
    
    return currentUV;
}

// ============================================
// Main Parallax Function
// ============================================
vec2 applyParallax(vec2 uv, vec3 worldPos, vec3 viewDir, mat3 TBN, int parallaxMode) {
    if (_EnableParallax < 0.5) {
        return uv;
    }
    
    // Transform view direction to tangent space
    mat3 TBN_T = transpose(TBN);
    vec3 viewDirTangent = normalize(TBN_T * viewDir);
    
    // Ensure we're looking down at the surface (positive Z in tangent space)
    if (viewDirTangent.z <= 0.0) {
        return uv;
    }
    
    // Apply texture transform
    vec2 transformedUV = uv * _ParallaxMap_ST.xy + _ParallaxMap_ST.zw;
    
    float heightScale = _ParallaxStrength;
    int minSamples = int(_ParallaxMinSamples);
    int maxSamples = int(_ParallaxMaxSamples);
    
    vec2 parallaxUV;
    
    if (parallaxMode == PARALLAX_SIMPLE) {
        parallaxUV = calculateSimpleParallax(transformedUV, viewDirTangent, heightScale);
    }
    else if (parallaxMode == PARALLAX_STEEP) {
        parallaxUV = calculateSteepParallax(transformedUV, viewDirTangent, heightScale, maxSamples);
    }
    else if (parallaxMode == PARALLAX_OCCLUSION) {
        parallaxUV = calculateParallaxOcclusion(transformedUV, viewDirTangent, heightScale, minSamples, maxSamples);
    }
    else if (parallaxMode == PARALLAX_RELIEF) {
        parallaxUV = calculateReliefParallax(transformedUV, viewDirTangent, heightScale, minSamples, maxSamples / 2);
    }
    else {
        parallaxUV = transformedUV;
    }
    
    // Transform back from ST space
    parallaxUV = (parallaxUV - _ParallaxMap_ST.zw) / _ParallaxMap_ST.xy;
    
    return parallaxUV;
}

// ============================================
// Parallax Self-Shadowing
// ============================================
float calculateParallaxShadow(vec2 uv, vec3 lightDirTangent, float heightScale, float currentHeight, int numSamples) {
    // Early out if light is behind surface
    if (lightDirTangent.z <= 0.0) {
        return 0.0;
    }
    
    // Calculate step size in light direction
    float layerDepth = currentHeight / float(numSamples);
    vec2 deltaUV = lightDirTangent.xy * heightScale / float(numSamples);
    
    vec2 currentUV = uv;
    float currentLayerDepth = currentHeight;
    float shadow = 0.0;
    
    for (int i = 0; i < numSamples; i++) {
        currentUV += deltaUV;
        float sampledHeight = sampleHeightMap(currentUV);
        currentLayerDepth -= layerDepth;
        
        if (sampledHeight > currentLayerDepth) {
            // In shadow
            float newShadow = (sampledHeight - currentLayerDepth);
            shadow = max(shadow, newShadow);
        }
    }
    
    return clamp(shadow * 2.0, 0.0, 1.0);
}

// ============================================
// Get Parallax Height at UV (for depth effects)
// ============================================
float getParallaxHeight(vec2 uv) {
    if (_EnableParallax < 0.5) {
        return 0.5;
    }
    return sampleHeightMap(uv * _ParallaxMap_ST.xy + _ParallaxMap_ST.zw);
}

// ============================================
// Contact Refinement (for silhouettes)
// ============================================
vec2 refineParallaxContact(vec2 uv, vec3 viewDirTangent, float heightScale, vec2 baseParallaxUV, int refinementSteps) {
    // Additional refinement near silhouette edges
    vec2 currentUV = baseParallaxUV;
    vec2 deltaUV = viewDirTangent.xy * heightScale / float(refinementSteps * 10);
    
    float currentHeight = sampleHeightMap(currentUV);
    float layerDepth = currentHeight;
    
    for (int i = 0; i < refinementSteps; i++) {
        currentUV -= deltaUV;
        float sampledHeight = sampleHeightMap(currentUV);
        
        if (sampledHeight < layerDepth - 0.01) {
            currentUV += deltaUV;
            break;
        }
        
        deltaUV *= 0.5;
    }
    
    return currentUV;
}

#endif // TOON_PARALLAX_GLSL
