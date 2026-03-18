#version 450

layout (location = 0) out vec4 OutColor;

uniform float MatSpecularIntensity;
uniform float MatShininess;
uniform float AlphaCutoff;

uniform vec3 CameraPosition;

uniform sampler2D Texture0;
uniform sampler2D Texture1;

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;
layout (location = 4) in vec2 FragUV0;

#pragma snippet "ForwardLighting"
#pragma snippet "AmbientOcclusionSampling"
#pragma snippet "ExactTransparencyDepthPeel"

void main()
{
    if (XRE_ShouldDiscardDepthPeelFragment())
        discard;

    float alphaMask = texture(Texture1, FragUV0).r;
    if (alphaMask < AlphaCutoff)
        discard;

    vec3 normal = normalize(FragNorm);
    vec4 texColor = texture(Texture0, FragUV0);
    float AmbientOcclusion = XRENGINE_SampleAmbientOcclusion();
    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, texColor.rgb, MatSpecularIntensity, AmbientOcclusion);
    OutColor = vec4(totalLight, texColor.a * alphaMask);
}