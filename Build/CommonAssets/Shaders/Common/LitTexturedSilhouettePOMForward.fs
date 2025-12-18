#version 450

layout (location = 0) out vec4 OutColor;

uniform float MatSpecularIntensity;
uniform float MatShininess;

uniform vec3 CameraPosition;

uniform sampler2D Texture0; // Albedo
uniform sampler2D Texture1; // Height (R)

// Parallax (silhouette POM) uniforms
uniform float ParallaxScale;        // UV units
uniform int ParallaxMinSteps;
uniform int ParallaxMaxSteps;
uniform int ParallaxRefineSteps;    // binary refinement steps
uniform float ParallaxHeightBias;   // applied to height before inversion
uniform float ParallaxSilhouette;   // >0.5 enables discard when UV exits [0,1]

layout (location = 0) in vec3 FragPos;
layout (location = 1) in vec3 FragNorm;
layout (location = 6) in vec2 FragUV0;

#pragma snippet "ForwardLighting"
#pragma snippet "ParallaxMapping"

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
    float AmbientOcclusion = 1.0;

    vec3 totalLight = XRENGINE_CalculateForwardLighting(normal, FragPos, texColor.rgb, MatSpecularIntensity, AmbientOcclusion);

    OutColor = texColor * vec4(totalLight, 1.0);
}
