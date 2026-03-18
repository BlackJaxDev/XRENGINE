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
uniform sampler2D Texture1; // Normal map (tangent space)
uniform sampler2D Texture2; // Alpha mask (R channel)

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;
layout (location = 3) in vec3 FragBinorm;
layout (location = 2) in vec3 FragTan;
layout (location = 4) in vec2 FragUV0;

#pragma snippet "ForwardLighting"
#pragma snippet "AmbientOcclusionSampling"
#pragma snippet "SurfaceDetailNormalMapping"
#pragma snippet "NormalEncoding"

vec3 getNormalFromMap()
{
    return XRENGINE_GetSurfaceDetailNormal(FragUV0, FragTan, FragBinorm, FragNorm);
}

void main()
{
    float alphaMask = texture(Texture2, FragUV0).r;
    if (alphaMask < AlphaCutoff)
        discard;

#if defined(XRENGINE_SHADOW_CASTER_PASS)
    Depth = gl_FragCoord.z;
#else
    vec3 normal = getNormalFromMap();

#if defined(XRENGINE_DEPTH_NORMAL_PREPASS)
    Normal = XRENGINE_EncodeNormal(normal);
#else
    vec4 texColor = texture(Texture0, FragUV0);
    float AmbientOcclusion = XRENGINE_SampleAmbientOcclusion();
    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, texColor.rgb, MatSpecularIntensity, AmbientOcclusion);

    OutColor = vec4(totalLight, texColor.a * alphaMask);
#endif
#endif
}