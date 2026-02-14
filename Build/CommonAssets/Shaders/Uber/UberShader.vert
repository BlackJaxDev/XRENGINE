// Uber Shader - Vertex Shader

#version 450 core

// ============================================
// Vertex Inputs
// ============================================
layout(location = 0) in vec3 a_Position;
layout(location = 1) in vec3 a_Normal;
layout(location = 2) in vec4 a_Tangent;     // xyz: tangent, w: bitangent sign
layout(location = 3) in vec2 a_TexCoord0;
layout(location = 4) in vec2 a_TexCoord1;
layout(location = 5) in vec2 a_TexCoord2;
layout(location = 6) in vec2 a_TexCoord3;
layout(location = 7) in vec4 a_Color;

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
    vec4 worldPosition = u_ModelMatrix * vec4(a_Position, 1.0);
    v_WorldPos = worldPosition.xyz;
    v_LocalPos = a_Position;
    
    // Output clip position
    gl_Position = u_ModelViewProjectionMatrix * vec4(a_Position, 1.0);
    
    // Transform normal to world space
    mat3 normalMatrix = mat3(transpose(inverse(u_ModelMatrix)));
    v_WorldNormal = normalize(normalMatrix * a_Normal);
    v_WorldTangent = normalize(normalMatrix * a_Tangent.xyz);
    v_TangentSign = a_Tangent.w;
    
    // Pass through UVs
    v_Uv01 = vec4(a_TexCoord0, a_TexCoord1);
    v_Uv23 = vec4(a_TexCoord2, a_TexCoord3);
    
    // Vertex color
    v_VertexColor = a_Color;
    
    // View direction (world space, pointing from surface to camera)
    v_ViewDir = normalize(u_CameraPosition - worldPosition.xyz);
}
