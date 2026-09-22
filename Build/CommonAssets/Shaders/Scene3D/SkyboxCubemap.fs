#version 450

#pragma snippet "DDGIEnvironmentCapture"

layout (location = 0) out vec4 OutColor;
layout (location = 1) in vec3 FragWorldDir;

uniform samplerCube Texture0;
uniform float SkyboxIntensity = 1.0;

void main()
{
    vec3 dir = XRENGINE_DDGIEnvironmentDirection(FragWorldDir);

    OutColor = vec4(texture(Texture0, dir).rgb * SkyboxIntensity, 1.0);
}
