// ===========================================
// Uber Shader - Outlines
// ===========================================
// Outline rendering using inverted hull method
// Requires a separate outline pass with front-face culling
// ===========================================

#ifndef TOON_OUTLINES_GLSL
#define TOON_OUTLINES_GLSL

// Outline Modes
#define OUTLINE_MODE_BASIC          0   // Simple vertex normal expansion
#define OUTLINE_MODE_TINT           1   // Tint based on base color
#define OUTLINE_MODE_RIMLIGHT       2   // Rim-style outline
#define OUTLINE_MODE_VERTEX_COLOR   3   // Use vertex color for width

// Outline Space
#define OUTLINE_SPACE_LOCAL         0   // Object space
#define OUTLINE_SPACE_CLIP          1   // Clip/Screen space (constant pixel width)
#define OUTLINE_SPACE_WORLD         2   // World space

// ============================================
// Outline Data Structure
// ============================================
struct OutlineData {
    bool enabled;
    vec4 color;
    float width;
    float emission;
    
    // Advanced
    int mode;
    int space;
    float tintMix;              // How much to mix with base color
    float distanceFade;         // Fade based on distance
    float minDistance;
    float maxDistance;
    
    // Mask
    float maskStrength;
    int maskChannel;
    
    // Lighting
    bool useLighting;
    float shadowStrength;
    
    // Offset
    float offset;               // Depth offset to prevent z-fighting
    
    // Vertex color control
    int vertexColorChannel;     // Which vertex color channel controls width
    float vertexColorWidth;     // Width multiplier from vertex color
};

// ============================================
// Outline Vertex Shader Functions
// ============================================

// Calculate outline offset in object space
vec3 calculateOutlineOffsetLocal(vec3 position, vec3 normal, float width) {
    return position + normal * width;
}

// Calculate outline offset in clip space (constant screen-space width)
vec4 calculateOutlineOffsetClip(vec4 clipPos, vec3 clipNormal, float width, vec2 screenSize) {
    vec2 offset = normalize(clipNormal.xy) * width * 2.0 / screenSize;
    clipPos.xy += offset * clipPos.w;
    return clipPos;
}

// Full outline vertex calculation
vec4 calculateOutlineVertex(
    vec3 localPos,
    vec3 localNormal,
    mat4 mvpMatrix,
    mat4 modelMatrix,
    OutlineData outline,
    vec4 vertexColor,
    vec2 screenSize,
    float cameraDistance
) {
    if (!outline.enabled || outline.width <= 0.0) {
        return mvpMatrix * vec4(localPos, 1.0);
    }
    
    float width = outline.width * 0.01; // Scale to reasonable units
    
    // Apply vertex color width control
    if (outline.vertexColorChannel >= 0 && outline.vertexColorChannel < 4) {
        width *= mix(1.0, vertexColor[outline.vertexColorChannel], outline.vertexColorWidth);
    }
    
    // Apply distance fade
    if (outline.distanceFade > 0.0) {
        float distFactor = smoothstep(outline.minDistance, outline.maxDistance, cameraDistance);
        width *= mix(1.0, 0.0, distFactor * outline.distanceFade);
    }
    
    vec4 outPos;
    
    switch (outline.space) {
        case OUTLINE_SPACE_LOCAL:
            // Object space - simple normal expansion
            vec3 expandedPos = localPos + localNormal * width;
            outPos = mvpMatrix * vec4(expandedPos, 1.0);
            break;
            
        case OUTLINE_SPACE_CLIP:
            // Clip space - constant screen width
            outPos = mvpMatrix * vec4(localPos, 1.0);
            vec3 viewNormal = normalize((mvpMatrix * vec4(localNormal, 0.0)).xyz);
            outPos.xy += normalize(viewNormal.xy) * width * outPos.w * 2.0 / screenSize;
            break;
            
        case OUTLINE_SPACE_WORLD:
            // World space
            vec3 worldNormal = normalize((modelMatrix * vec4(localNormal, 0.0)).xyz);
            vec3 worldPos = (modelMatrix * vec4(localPos, 1.0)).xyz;
            worldPos += worldNormal * width;
            outPos = mvpMatrix * inverse(modelMatrix) * vec4(worldPos, 1.0);
            break;
            
        default:
            outPos = mvpMatrix * vec4(localPos + localNormal * width, 1.0);
    }
    
    // Apply depth offset to prevent z-fighting
    outPos.z += outline.offset * 0.0001;
    
    return outPos;
}

// ============================================
// Outline Fragment Shader Functions
// ============================================

vec4 calculateOutlineColor(
    OutlineData outline,
    vec3 baseColor,
    vec3 lightColor,
    float shadow,
    float maskValue
) {
    if (!outline.enabled) {
        discard;
    }
    
    vec3 outlineColor = outline.color.rgb;
    float outlineAlpha = outline.color.a;
    
    // Apply mask
    outlineAlpha *= mix(1.0, maskValue, outline.maskStrength);
    
    if (outlineAlpha < 0.001) {
        discard;
    }
    
    // Mix with base color based on mode
    switch (outline.mode) {
        case OUTLINE_MODE_TINT:
            outlineColor = mix(outlineColor, outlineColor * baseColor, outline.tintMix);
            break;
        case OUTLINE_MODE_RIMLIGHT:
            outlineColor *= baseColor;
            break;
        case OUTLINE_MODE_VERTEX_COLOR:
            // Already handled in vertex shader
            break;
    }
    
    // Apply lighting if enabled
    if (outline.useLighting) {
        float shadowFactor = mix(1.0, shadow, outline.shadowStrength);
        outlineColor *= lightColor * shadowFactor;
    }
    
    return vec4(outlineColor, outlineAlpha);
}

// Calculate emission from outline
vec3 getOutlineEmission(OutlineData outline, vec3 outlineColor) {
    return outlineColor * outline.emission;
}

#endif // TOON_OUTLINES_GLSL
