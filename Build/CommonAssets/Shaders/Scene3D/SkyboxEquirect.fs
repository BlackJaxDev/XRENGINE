#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 1) in vec3 FragWorldDir;

uniform sampler2D Texture0;
uniform float SkyboxIntensity = 1.0;

const float PI = 3.14159265359;

void main()
{
    vec3 dir = normalize(FragWorldDir);

    // Convert direction to spherical coordinates
    float phi = atan(dir.z, dir.x);
    float theta = asin(clamp(dir.y, -1.0, 1.0));
    
    // Map to UV coordinates
    vec2 uv = vec2((phi / (2.0 * PI)) + 0.5, 1.0 - ((theta / PI) + 0.5));

    OutColor = texture(Texture0, uv).rgb * SkyboxIntensity;
}
