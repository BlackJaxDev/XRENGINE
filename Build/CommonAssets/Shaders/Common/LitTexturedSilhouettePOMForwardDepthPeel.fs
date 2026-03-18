#version 450

layout (location = 0) out vec4 OutColor;

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
#pragma snippet "ExactTransparencyDepthPeel"

void main()
{
    if (XRE_ShouldDiscardDepthPeelFragment())
        discard;

    vec3 normal = normalize(FragNorm);
    vec3 viewDirWS = normalize(CameraPosition - FragPos);
    mat3 tbn = XRENGINE_ComputeTBN(normal, FragPos, FragUV0);
    vec3 viewDirTS = transpose(tbn) * viewDirWS;
    bool pomValid;
    vec2 uv = XRENGINE_SilhouetteParallaxOcclusionMapping(Texture1, FragUV0, viewDirTS, ParallaxScale, ParallaxMinSteps, ParallaxMaxSteps, ParallaxRefineSteps, ParallaxHeightBias, pomValid);
    if (ParallaxSilhouette > 0.5 && !pomValid)
        discard;
    if (ParallaxSilhouette <= 0.5 && !pomValid)
        uv = FragUV0;

    vec4 texColor = texture(Texture0, uv);
    float AmbientOcclusion = XRENGINE_SampleAmbientOcclusion();
    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, texColor.rgb, MatSpecularIntensity, AmbientOcclusion);
    OutColor = vec4(totalLight, texColor.a);
}