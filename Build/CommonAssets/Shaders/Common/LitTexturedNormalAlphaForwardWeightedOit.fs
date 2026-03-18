#version 450

layout (location = 0) out vec4 OutAccum;
layout (location = 1) out vec4 OutRevealage;

uniform float MatSpecularIntensity;
uniform float MatShininess;
uniform float AlphaCutoff;

uniform vec3 CameraPosition;

uniform sampler2D Texture0;
uniform sampler2D Texture1;
uniform sampler2D Texture2;

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;
layout (location = 3) in vec3 FragBinorm;
layout (location = 2) in vec3 FragTan;
layout (location = 4) in vec2 FragUV0;

#pragma snippet "ForwardLighting"
#pragma snippet "AmbientOcclusionSampling"
#pragma snippet "SurfaceDetailNormalMapping"

float XRE_ComputeOitWeight(float alpha)
{
    float depthWeight = clamp(1.0 - gl_FragCoord.z * 0.85, 0.05, 1.0);
    return clamp(alpha * (0.25 + depthWeight * depthWeight * 4.0), 1e-2, 8.0);
}

void XRE_WriteWeightedBlendedOit(vec4 shadedColor)
{
    float alpha = clamp(shadedColor.a, 0.0, 1.0);
    if (alpha <= 0.0001)
        discard;

    float weight = XRE_ComputeOitWeight(alpha);
    vec3 premultiplied = shadedColor.rgb * alpha;
    OutAccum = vec4(premultiplied * weight, alpha * weight);
    OutRevealage = vec4(alpha);
}

vec3 getNormalFromMap()
{
    return XRENGINE_GetSurfaceDetailNormal(FragUV0, FragTan, FragBinorm, FragNorm);
}

void main()
{
    float alphaMask = texture(Texture2, FragUV0).r;
    if (alphaMask < AlphaCutoff)
        discard;

    vec4 texColor = texture(Texture0, FragUV0);
    float AmbientOcclusion = XRENGINE_SampleAmbientOcclusion();
    vec3 normal = getNormalFromMap();
    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, texColor.rgb, MatSpecularIntensity, AmbientOcclusion);
    XRE_WriteWeightedBlendedOit(vec4(totalLight, texColor.a * alphaMask));
}