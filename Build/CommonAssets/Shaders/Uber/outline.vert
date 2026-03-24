// Outline Vertex Shader - Separate Pass
// GLSL 450 - Inverted Hull Method
#version 450 core

out gl_PerVertex {
    vec4 gl_Position;
    float gl_PointSize;
    float gl_ClipDistance[];
};

// ============================================
// Vertex Attributes
// ============================================
layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;
layout(location = 2) in vec4 Tangent;
layout(location = 3) in vec2 TexCoord0;
layout(location = 4) in vec2 TexCoord1;
layout(location = 5) in vec4 Color0;

// ============================================
// Uniforms (using engine-standard names)
// ============================================
// Transform matrices (engine provides these with _VTX suffix for vertex stage)
uniform mat4 ModelMatrix;
uniform mat4 ViewMatrix_VTX;
uniform mat4 ProjMatrix_VTX;

// Convenience macros for compatibility
#define u_ModelMatrix ModelMatrix
#define u_ViewMatrix ViewMatrix_VTX
#define u_ProjectionMatrix ProjMatrix_VTX
#define u_MVPMatrix (ProjMatrix_VTX * ViewMatrix_VTX * ModelMatrix)
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
layout(location = 0) out vec2 v_Uv;
layout(location = 1) out vec4 v_VertexColor;
layout(location = 2) out vec3 v_WorldPos;
layout(location = 3) out float v_DistanceFade;

// ============================================
// Main
// ============================================
void main() {
    // Transform position to world space
    vec4 worldPos = u_ModelMatrix * vec4(Position, 1.0);
    v_WorldPos = worldPos.xyz;
    
    // Transform normal to world space
    vec3 worldNormal = normalize(u_NormalMatrix * Normal);
    
    // Calculate UV
    v_Uv = TexCoord0 * _MainTex_ST.xy + _MainTex_ST.zw;
    v_VertexColor = Color0;
    
    // Sample outline mask (if we had access to textures in vertex shader)
    // In practice, you might use vertex colors for this
    float outlineMask = 1.0;
    
    // Vertex color width modulation (using alpha channel)
    float vertexColorWidth = mix(1.0, Color0.a, _OutlineVertexColorWidth);
    
    // Calculate distance from camera for distance fade
    float dist = length(u_CameraPosition - worldPos.xyz);
    
    // Distance fade factor
    float distanceFade = 1.0;
    if (_OutlineDistanceFadeEnd > _OutlineDistanceFadeStart) {
        distanceFade = 1.0 - smoothstep(_OutlineDistanceFadeStart, _OutlineDistanceFadeEnd, dist);
    }
    v_DistanceFade = distanceFade;
    
    // Calculate outline width with modulations
    float finalWidth = _OutlineWidth * outlineMask * vertexColorWidth * distanceFade;
    
    // Convert width from world units to clip space
    // This ensures consistent width regardless of distance
    vec4 clipPos = u_MVPMatrix * vec4(Position, 1.0);
    vec4 clipNormal = u_MVPMatrix * vec4(Position + worldNormal * 0.01, 1.0);
    vec2 screenNormal = normalize((clipNormal.xy / clipNormal.w) - (clipPos.xy / clipPos.w));
    
    // Expand vertex along normal in clip space
    // Scale by W to maintain consistent screen-space width
    float aspect = u_ProjectionMatrix[1][1] / u_ProjectionMatrix[0][0];
    vec2 offset = screenNormal * finalWidth * 0.01;
    offset.x /= aspect;
    
    // Alternative: World space expansion (simpler, but width varies with distance)
    vec3 expandedPos = Position + Normal * finalWidth * 0.01;
    
    // Final position
    gl_Position = u_MVPMatrix * vec4(expandedPos, 1.0);
    
    // Offset in clip space for more consistent width
    // gl_Position.xy += offset * gl_Position.w;
}
