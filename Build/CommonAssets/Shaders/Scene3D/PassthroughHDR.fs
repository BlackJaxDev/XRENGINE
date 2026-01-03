#version 450 core

// Simple passthrough shader for HDR textures
layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D HDRSceneTex;

void main()
{
    vec2 uv = FragPos.xy;
    vec4 color = texture(HDRSceneTex, uv);
    // Simple tonemapping for display
    color.rgb = color.rgb / (color.rgb + 1.0);
    OutColor = vec4(color.rgb, 1.0);
}
