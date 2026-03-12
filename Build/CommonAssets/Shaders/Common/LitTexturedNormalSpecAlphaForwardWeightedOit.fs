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
uniform sampler2D Texture3;

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;
layout (location = 2) in vec3 FragBinorm;
layout (location = 3) in vec3 FragTan;
layout (location = 4) in vec2 FragUV0;

#pragma snippet "ForwardLighting"
#pragma snippet "AmbientOcclusionSampling"

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
    vec3 normal = texture(Texture1, FragUV0).rgb;
    normal = normalize(normal * 2.0 - 1.0);
    vec3 T = normalize(FragTan);
    vec3 N = normalize(FragNorm);
    vec3 B = cross(N, T);
    mat3 tbn = mat3(T, B, N);
    return normalize(tbn * normal);
}

void main()
{
    float alphaMask = texture(Texture3, FragUV0).r;
    if (alphaMask < AlphaCutoff)
        discard;

    vec4 texColor = texture(Texture0, FragUV0);
    float AmbientOcclusion = XRENGINE_SampleAmbientOcclusion();
    float specularMask = texture(Texture2, FragUV0).r;
    float specIntensity = MatSpecularIntensity * specularMask;
    vec3 normal = getNormalFromMap();
    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, texColor.rgb, specIntensity, AmbientOcclusion);
    XRE_WriteWeightedBlendedOit(vec4(texColor.rgb * totalLight, texColor.a * alphaMask));
}