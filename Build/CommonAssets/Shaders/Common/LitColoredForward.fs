#version 450

layout (location = 0) out vec4 OutColor;

uniform vec4 MatColor;
uniform float MatSpecularIntensity;
uniform float MatShininess;

uniform vec3 CameraPosition;
uniform vec3 CameraForward;

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;

#pragma snippet "ForwardLighting"

void main()
{
    vec3 normal = normalize(FragNorm);

    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, MatColor.rgb, MatSpecularIntensity, 1.0);

    OutColor = MatColor * vec4(totalLight, 1.0);
}
