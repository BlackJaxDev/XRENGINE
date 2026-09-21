#version 450

#pragma snippet "DDGIEnvironmentCapture"

layout (location = 0) out vec3 OutColor;
layout (location = 1) in vec3 FragWorldDir;

uniform samplerCubeArray Texture0;
uniform float SkyboxIntensity = 1.0;
uniform int CubemapLayer = 0;

void main()
{
    vec3 dir = XRENGINE_DDGIEnvironmentDirection(FragWorldDir);

    OutColor = texture(Texture0, vec4(dir, float(CubemapLayer))).rgb * SkyboxIntensity;
}
