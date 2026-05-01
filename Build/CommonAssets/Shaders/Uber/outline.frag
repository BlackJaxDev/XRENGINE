// Outline Fragment Shader - Separate Pass
// GLSL 450
#version 450 core

#include "common.glsl"
#pragma snippet "LightStructs"

// ============================================
// Fragment Inputs
// ============================================
layout(location = 0) in vec2 v_Uv;
layout(location = 1) in vec4 v_VertexColor;
layout(location = 2) in vec3 v_WorldPos;
layout(location = 3) in float v_DistanceFade;

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

uniform vec3 GlobalAmbient;
uniform int DirLightCount; 

layout(std430, binding = 22) readonly buffer ForwardDirectionalLightsBuffer
{
    DirLight DirectionalLights[];
};

// Compatibility macros
#define u_LightDirection (DirLightCount > 0 ? DirectionalLights[0].Direction : vec3(0.0, -1.0, 0.0))
#define u_LightColor (DirLightCount > 0 ? DirectionalLights[0].Base.Color : vec3(1.0))
#define u_AmbientColor GlobalAmbient

// ============================================
// Main
// ============================================
void main() {
    // Start with outline base color
    vec4 outlineColor = _OutlineColor;
    
    // Apply texture tinting
    if (_OutlineTextureTint > 0.0) {
        vec4 mainTex = texture(_MainTex, v_Uv);
        vec4 texColor = mainTex * _Color;
        outlineColor.rgb = mix(outlineColor.rgb, outlineColor.rgb * texColor.rgb, _OutlineTextureTint);
    }
    
    // Apply vertex color tinting
    if (_OutlineVertexColorTint > 0.0) {
        outlineColor.rgb = mix(outlineColor.rgb, outlineColor.rgb * v_VertexColor.rgb, _OutlineVertexColorTint);
    }
    
    // Apply lighting (if lit outlines enabled)
    if (_OutlineLit > 0.0) {
        // Simple lambert lighting
        vec3 lightDir = normalize(u_LightDirection);
        float NdotL = max(dot(normalize(cross(dFdx(v_WorldPos), dFdy(v_WorldPos))), lightDir), 0.0);
        vec3 lighting = u_AmbientColor + u_LightColor * NdotL;
        outlineColor.rgb = mix(outlineColor.rgb, outlineColor.rgb * lighting, _OutlineLit);
    }
    
    // Add emission
    outlineColor.rgb += outlineColor.rgb * _OutlineEmission;
    
    // Apply distance fade to alpha
    outlineColor.a *= v_DistanceFade;
    
    // Discard if alpha is zero (optimization)
    if (outlineColor.a < 0.001) {
        discard;
    }
    
    FragColor = outlineColor;
}
