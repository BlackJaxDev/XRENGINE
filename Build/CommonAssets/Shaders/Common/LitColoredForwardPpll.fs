#version 450

uniform vec4 MatColor;
uniform float MatSpecularIntensity;
uniform float MatShininess;

uniform vec3 CameraPosition;
uniform vec3 CameraForward;

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;

#pragma snippet "ForwardLighting"
#pragma snippet "AmbientOcclusionSampling"
#pragma snippet "ExactTransparencyPpll"

void main()
{
    vec3 normal = normalize(FragNorm);
    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, MatColor.rgb, MatSpecularIntensity, XRENGINE_SampleAmbientOcclusion());
    XRE_StorePerPixelLinkedListFragment(MatColor * vec4(totalLight, 1.0));
}