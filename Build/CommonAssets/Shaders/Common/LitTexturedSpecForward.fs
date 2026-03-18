#version 450

#ifdef XRENGINE_DEPTH_NORMAL_PREPASS
layout (location = 0) out vec2 Normal;
#else
layout (location = 0) out vec4 OutColor;
#endif

uniform float MatSpecularIntensity;
uniform float MatShininess;

uniform vec3 CameraPosition;

uniform sampler2D Texture0; // Albedo (diffuse)
uniform sampler2D Texture1; // Specular map (intensity in R channel)

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;
layout (location = 4) in vec2 FragUV0;

#pragma snippet "ForwardLighting"
#pragma snippet "AmbientOcclusionSampling"
#pragma snippet "NormalEncoding"

void main()
{
    vec3 normal = normalize(FragNorm);

#ifdef XRENGINE_DEPTH_NORMAL_PREPASS
    Normal = XRENGINE_EncodeNormal(normal);
#else
    vec4 texColor = texture(Texture0, FragUV0);
    float AmbientOcclusion = XRENGINE_SampleAmbientOcclusion();

    float specularMask = texture(Texture1, FragUV0).r;
    float specIntensity = MatSpecularIntensity * specularMask;

    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, texColor.rgb, specIntensity, AmbientOcclusion);

    OutColor = vec4(totalLight, texColor.a);
#endif
}