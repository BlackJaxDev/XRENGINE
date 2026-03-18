#version 450

#ifdef XRENGINE_DEPTH_NORMAL_PREPASS
layout (location = 0) out vec2 Normal;
#else
layout (location = 0) out vec4 OutColor;
#endif

uniform vec4 MatColor;
uniform float MatSpecularIntensity;
uniform float MatShininess;

uniform vec3 CameraPosition;
uniform vec3 CameraForward;

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;

#pragma snippet "ForwardLighting"
#pragma snippet "AmbientOcclusionSampling"
#pragma snippet "NormalEncoding"

void main()
{
    vec3 normal = normalize(FragNorm);

#ifdef XRENGINE_DEPTH_NORMAL_PREPASS
    Normal = XRENGINE_EncodeNormal(normal);
#else

    vec3 totalLight = XRENGINE_CalculateForwardLighting(
        normal,
        FragPos,
        MatColor.rgb,
        MatSpecularIntensity,
        XRENGINE_SampleAmbientOcclusion());

    OutColor = vec4(totalLight, MatColor.a);
#endif
}
