// Outline Fragment Shader - Separate Pass
// GLSL 450
#version 450 core

#include "common.glsl"

// ============================================
// Fragment Inputs
// ============================================
in VS_OUT {
    vec2 uv;
    vec4 vertexColor;
    vec3 worldPos;
    float distanceFade;
} fs_in;

// ============================================
// Fragment Output
// ============================================
layout(location = 0) out vec4 FragColor;

// ============================================
// Uniforms
// ============================================
// Main texture (for tinting)
uniform sampler2D _MainTex;
uniform vec4 _MainTex_ST;
uniform vec4 _Color;

// Outline parameters
uniform vec4 _OutlineColor;
uniform float _OutlineEmission;
uniform float _OutlineLit;
uniform float _OutlineTextureTint;
uniform float _OutlineVertexColorTint;

// Lighting (for lit outlines)
uniform vec3 u_LightDirection;
uniform vec3 u_LightColor;
uniform vec3 u_AmbientColor;

// ============================================
// Main
// ============================================
void main() {
    // Start with outline base color
    vec4 outlineColor = _OutlineColor;
    
    // Apply texture tinting
    if (_OutlineTextureTint > 0.0) {
        vec4 mainTex = texture(_MainTex, fs_in.uv);
        vec4 texColor = mainTex * _Color;
        outlineColor.rgb = mix(outlineColor.rgb, outlineColor.rgb * texColor.rgb, _OutlineTextureTint);
    }
    
    // Apply vertex color tinting
    if (_OutlineVertexColorTint > 0.0) {
        outlineColor.rgb = mix(outlineColor.rgb, outlineColor.rgb * fs_in.vertexColor.rgb, _OutlineVertexColorTint);
    }
    
    // Apply lighting (if lit outlines enabled)
    if (_OutlineLit > 0.0) {
        // Simple lambert lighting
        vec3 lightDir = normalize(u_LightDirection);
        float NdotL = max(dot(normalize(cross(dFdx(fs_in.worldPos), dFdy(fs_in.worldPos))), lightDir), 0.0);
        vec3 lighting = u_AmbientColor + u_LightColor * NdotL;
        outlineColor.rgb = mix(outlineColor.rgb, outlineColor.rgb * lighting, _OutlineLit);
    }
    
    // Add emission
    outlineColor.rgb += outlineColor.rgb * _OutlineEmission;
    
    // Apply distance fade to alpha
    outlineColor.a *= fs_in.distanceFade;
    
    // Discard if alpha is zero (optimization)
    if (outlineColor.a < 0.001) {
        discard;
    }
    
    FragColor = outlineColor;
}
