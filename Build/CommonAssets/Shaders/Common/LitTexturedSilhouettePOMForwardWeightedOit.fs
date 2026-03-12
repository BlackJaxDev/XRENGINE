#version 450

layout (location = 0) out vec4 OutAccum;
layout (location = 1) out vec4 OutRevealage;

uniform float MatSpecularIntensity;
uniform float MatShininess;

uniform vec3 CameraPosition;

uniform sampler2D Texture0;
uniform sampler2D Texture1;

uniform float ParallaxScale;
uniform int ParallaxMinSteps;
uniform int ParallaxMaxSteps;
uniform int ParallaxRefineSteps;
uniform float ParallaxHeightBias;
uniform float ParallaxSilhouette;

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;
layout (location = 4) in vec2 FragUV0;

#pragma snippet "ForwardLighting"
#pragma snippet "AmbientOcclusionSampling"
#pragma snippet "ParallaxMapping"

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

void main()
{
    vec3 normal = normalize(FragNorm);
    vec3 viewDirWS = normalize(CameraPosition - FragPos);
    mat3 tbn = XRENGINE_ComputeTBN(normal, FragPos, FragUV0);
    vec3 viewDirTS = transpose(tbn) * viewDirWS;

    bool pomValid;
    vec2 uv = XRENGINE_SilhouetteParallaxOcclusionMapping(
        Texture1,
        FragUV0,
        viewDirTS,
        ParallaxScale,
        ParallaxMinSteps,
        ParallaxMaxSteps,
        ParallaxRefineSteps,
        ParallaxHeightBias,
        pomValid);

    if (ParallaxSilhouette > 0.5 && !pomValid)
        discard;

    if (ParallaxSilhouette <= 0.5 && !pomValid)
        uv = FragUV0;

    vec4 texColor = texture(Texture0, uv);
    float AmbientOcclusion = XRENGINE_SampleAmbientOcclusion();
    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, texColor.rgb, MatSpecularIntensity, AmbientOcclusion);
    XRE_WriteWeightedBlendedOit(texColor * vec4(totalLight, 1.0));
}