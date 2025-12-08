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
out VS_OUT {
    vec4 uv01;              // xy: uv0, zw: uv1
    vec4 uv23;              // xy: uv2, zw: uv3
    vec3 worldPos;
    vec3 worldNormal;
    vec3 worldTangent;
    float tangentSign;
    vec4 vertexColor;
    vec3 localPos;
    vec3 viewDir;
} vs_out;

// ============================================
// Uniforms
// ============================================
#include "uniforms.glsl"

void main() {
    // Transform position
    vec4 worldPosition = u_ModelMatrix * vec4(a_Position, 1.0);
    vs_out.worldPos = worldPosition.xyz;
    vs_out.localPos = a_Position;
    
    // Output clip position
    gl_Position = u_ModelViewProjectionMatrix * vec4(a_Position, 1.0);
    
    // Transform normal to world space
    mat3 normalMatrix = mat3(transpose(inverse(u_ModelMatrix)));
    vs_out.worldNormal = normalize(normalMatrix * a_Normal);
    vs_out.worldTangent = normalize(normalMatrix * a_Tangent.xyz);
    vs_out.tangentSign = a_Tangent.w;
    
    // Pass through UVs
    vs_out.uv01 = vec4(a_TexCoord0, a_TexCoord1);
    vs_out.uv23 = vec4(a_TexCoord2, a_TexCoord3);
    
    // Vertex color
    vs_out.vertexColor = a_Color;
    
    // View direction (world space, pointing from surface to camera)
    vs_out.viewDir = normalize(u_CameraPosition - worldPosition.xyz);
}
