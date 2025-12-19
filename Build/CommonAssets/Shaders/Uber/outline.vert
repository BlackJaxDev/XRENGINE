// Outline Vertex Shader - Separate Pass
// GLSL 450 - Inverted Hull Method
#version 450 core

// ============================================
// Vertex Attributes
// ============================================
layout(location = 0) in vec3 a_Position;
layout(location = 1) in vec3 a_Normal;
layout(location = 2) in vec4 a_Tangent;
layout(location = 3) in vec2 a_TexCoord0;
layout(location = 4) in vec2 a_TexCoord1;
layout(location = 5) in vec4 a_Color;

// ============================================
// Uniforms (using engine-standard names)
// ============================================
// Transform matrices (engine provides these)
uniform mat4 ModelMatrix;
uniform mat4 ViewMatrix;
uniform mat4 ProjMatrix;

// Convenience macros for compatibility
#define u_ModelMatrix ModelMatrix
#define u_ViewMatrix ViewMatrix
#define u_ProjectionMatrix ProjMatrix
#define u_MVPMatrix (ProjMatrix * ViewMatrix * ModelMatrix)
#define u_NormalMatrix mat3(transpose(inverse(ModelMatrix)))

// Camera data
uniform vec3 CameraPosition;
#define u_CameraPosition CameraPosition

// Outline parameters
uniform float _OutlineWidth;
uniform sampler2D _OutlineMask;
uniform float _OutlineDistanceFadeStart;
uniform float _OutlineDistanceFadeEnd;
uniform float _OutlineVertexColorWidth; // Use vertex color for width modulation

// Texture transform
uniform vec4 _MainTex_ST;

// ============================================
// Vertex Outputs
// ============================================
out VS_OUT {
    vec2 uv;
    vec4 vertexColor;
    vec3 worldPos;
    float distanceFade;
} vs_out;

// ============================================
// Main
// ============================================
void main() {
    // Transform position to world space
    vec4 worldPos = u_ModelMatrix * vec4(a_Position, 1.0);
    vs_out.worldPos = worldPos.xyz;
    
    // Transform normal to world space
    vec3 worldNormal = normalize(u_NormalMatrix * a_Normal);
    
    // Calculate UV
    vs_out.uv = a_TexCoord0 * _MainTex_ST.xy + _MainTex_ST.zw;
    vs_out.vertexColor = a_Color;
    
    // Sample outline mask (if we had access to textures in vertex shader)
    // In practice, you might use vertex colors for this
    float outlineMask = 1.0;
    
    // Vertex color width modulation (using alpha channel)
    float vertexColorWidth = mix(1.0, a_Color.a, _OutlineVertexColorWidth);
    
    // Calculate distance from camera for distance fade
    float dist = length(u_CameraPosition - worldPos.xyz);
    
    // Distance fade factor
    float distanceFade = 1.0;
    if (_OutlineDistanceFadeEnd > _OutlineDistanceFadeStart) {
        distanceFade = 1.0 - smoothstep(_OutlineDistanceFadeStart, _OutlineDistanceFadeEnd, dist);
    }
    vs_out.distanceFade = distanceFade;
    
    // Calculate outline width with modulations
    float finalWidth = _OutlineWidth * outlineMask * vertexColorWidth * distanceFade;
    
    // Convert width from world units to clip space
    // This ensures consistent width regardless of distance
    vec4 clipPos = u_MVPMatrix * vec4(a_Position, 1.0);
    vec4 clipNormal = u_MVPMatrix * vec4(a_Position + worldNormal * 0.01, 1.0);
    vec2 screenNormal = normalize((clipNormal.xy / clipNormal.w) - (clipPos.xy / clipPos.w));
    
    // Expand vertex along normal in clip space
    // Scale by W to maintain consistent screen-space width
    float aspect = u_ProjectionMatrix[1][1] / u_ProjectionMatrix[0][0];
    vec2 offset = screenNormal * finalWidth * 0.01;
    offset.x /= aspect;
    
    // Alternative: World space expansion (simpler, but width varies with distance)
    vec3 expandedPos = a_Position + a_Normal * finalWidth * 0.01;
    
    // Final position
    gl_Position = u_MVPMatrix * vec4(expandedPos, 1.0);
    
    // Offset in clip space for more consistent width
    // gl_Position.xy += offset * gl_Position.w;
}
