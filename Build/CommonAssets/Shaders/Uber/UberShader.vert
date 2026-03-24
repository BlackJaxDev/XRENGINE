// Uber Shader - Vertex Shader

#version 450 core

out gl_PerVertex {
    vec4 gl_Position;
    float gl_PointSize;
    float gl_ClipDistance[];
};

// ============================================
// Vertex Inputs
// ============================================
layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;
layout(location = 2) in vec4 Tangent;     // xyz: tangent, w: bitangent sign
layout(location = 3) in vec2 TexCoord0;
layout(location = 4) in vec2 TexCoord1;
layout(location = 5) in vec2 TexCoord2;
layout(location = 6) in vec2 TexCoord3;
layout(location = 7) in vec4 Color0;

// ============================================
// Vertex Outputs
// ============================================
layout(location = 0) out vec4 v_Uv01;              // xy: uv0, zw: uv1
layout(location = 1) out vec4 v_Uv23;              // xy: uv2, zw: uv3
layout(location = 2) out vec3 v_WorldPos;
layout(location = 3) out vec3 v_WorldNormal;
layout(location = 4) out vec3 v_WorldTangent;
layout(location = 5) out float v_TangentSign;
layout(location = 6) out vec4 v_VertexColor;
layout(location = 7) out vec3 v_LocalPos;
layout(location = 8) out vec3 v_ViewDir;

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
    v_VertexColor = Color0;
    
    // View direction (world space, pointing from surface to camera)
    v_ViewDir = normalize(u_CameraPosition - worldPosition.xyz);
}
