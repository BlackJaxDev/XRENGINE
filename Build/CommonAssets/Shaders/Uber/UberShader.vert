// Uber Shader - Vertex Shader

#version 450 core

// ============================================
// Vertex Inputs
// ============================================
in vec3 Position;
in vec3 Normal;
in vec4 Tangent;     // xyz: tangent, w: bitangent sign
in vec2 TexCoord0;
in vec2 TexCoord1;
in vec2 TexCoord2;
in vec2 TexCoord3;
in vec4 Color;

// ============================================
// Vertex Outputs
// ============================================
out vec4 v_Uv01;              // xy: uv0, zw: uv1
out vec4 v_Uv23;              // xy: uv2, zw: uv3
out vec3 v_WorldPos;
out vec3 v_WorldNormal;
out vec3 v_WorldTangent;
out float v_TangentSign;
out vec4 v_VertexColor;
out vec3 v_LocalPos;
out vec3 v_ViewDir;

// ============================================
// Uniforms
// ============================================
#include "uniforms.glsl"

void main() {
    // Transform position
    vec4 worldPosition = u_ModelMatrix * vec4(Position, 1.0);
    v_WorldPos = worldPosition.xyz;
    v_LocalPos = Position;
    
    // Output clip position
    gl_Position = u_ModelViewProjectionMatrix * vec4(Position, 1.0);
    
    // Transform normal to world space
    mat3 normalMatrix = mat3(transpose(inverse(u_ModelMatrix)));
    v_WorldNormal = normalize(normalMatrix * Normal);
    v_WorldTangent = normalize(normalMatrix * Tangent.xyz);
    v_TangentSign = Tangent.w;
    
    // Pass through UVs
    v_Uv01 = vec4(TexCoord0, TexCoord1);
    v_Uv23 = vec4(TexCoord2, TexCoord3);
    
    // Vertex color
    v_VertexColor = Color;
    
    // View direction (world space, pointing from surface to camera)
    v_ViewDir = normalize(u_CameraPosition - worldPosition.xyz);
}
