#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 1) in vec3 FragWorldDir;

uniform float SkyboxIntensity = 1.0;
uniform vec3 SkyboxTopColor = vec3(0.52, 0.74, 1.0);
uniform vec3 SkyboxBottomColor = vec3(0.05, 0.06, 0.08);

void main()
{
    vec3 dir = normalize(FragWorldDir);
    float t = clamp(dir.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 col = mix(SkyboxBottomColor, SkyboxTopColor, t);
    OutColor = col * SkyboxIntensity;
}
