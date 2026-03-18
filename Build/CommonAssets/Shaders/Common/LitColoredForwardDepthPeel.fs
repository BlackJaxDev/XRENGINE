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
#pragma snippet "AmbientOcclusionSampling"
#pragma snippet "ExactTransparencyDepthPeel"

void main()
{
    if (XRE_ShouldDiscardDepthPeelFragment())
        discard;

    vec3 normal = normalize(FragNorm);
    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, MatColor.rgb, MatSpecularIntensity, XRENGINE_SampleAmbientOcclusion());
    OutColor = vec4(totalLight, MatColor.a);
}