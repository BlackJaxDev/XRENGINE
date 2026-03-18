#version 450

#if defined(XRENGINE_DEPTH_NORMAL_PREPASS)
layout (location = 0) out vec2 Normal;
#elif defined(XRENGINE_SHADOW_CASTER_PASS)
layout (location = 0) out float Depth;
#else
layout (location = 0) out vec4 OutColor;
#endif

uniform float MatSpecularIntensity;
uniform float MatShininess;
uniform float AlphaCutoff;

uniform vec3 CameraPosition;

uniform sampler2D Texture0; // Albedo (diffuse)
uniform sampler2D Texture1; // Specular map (intensity in R channel)
uniform sampler2D Texture2; // Alpha mask (R channel)

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;
layout (location = 4) in vec2 FragUV0;

#pragma snippet "ForwardLighting"
#pragma snippet "AmbientOcclusionSampling"
#pragma snippet "NormalEncoding"

void main()
{
    // Sample alpha mask first for early discard
    float alphaMask = texture(Texture2, FragUV0).r;
    if (alphaMask < AlphaCutoff)
        discard;

#if defined(XRENGINE_SHADOW_CASTER_PASS)
    Depth = gl_FragCoord.z;
#else
    vec3 normal = normalize(FragNorm);

#if defined(XRENGINE_DEPTH_NORMAL_PREPASS)
    Normal = XRENGINE_EncodeNormal(normal);
#else
    vec4 texColor = texture(Texture0, FragUV0);
    float AmbientOcclusion = XRENGINE_SampleAmbientOcclusion();

    // Sample specular map (use R channel as intensity)
    float specularMask = texture(Texture1, FragUV0).r;
    float specIntensity = MatSpecularIntensity * specularMask;

    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, texColor.rgb, specIntensity, AmbientOcclusion);

    OutColor = vec4(totalLight, texColor.a * alphaMask);
#endif
#endif
}
