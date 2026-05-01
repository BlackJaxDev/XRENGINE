#pragma snippet "LightStructs"
#pragma snippet "LightAttenuation"
#pragma snippet "ShadowSampling"

uniform vec3 CameraPosition;
uniform vec3 GlobalAmbient;

// Primary directional light shadow map (bound separately)
// Use explicit binding at unit 15 to avoid collision with material textures.
layout(binding = 15) uniform sampler2D ShadowMap;
uniform bool ShadowMapEnabled;

uniform int DirLightCount; 
uniform int SpotLightCount;
uniform int PointLightCount;

layout(std430, binding = 22) readonly buffer ForwardDirectionalLightsBuffer
{
    DirLight DirectionalLights[];
};

layout(std430, binding = 23) readonly buffer ForwardPointLightsBuffer
{
    PointLight PointLights[];
};

layout(std430, binding = 26) readonly buffer ForwardSpotLightsBuffer
{
    SpotLight SpotLights[];
};

//0 is fully in shadow, 1 is fully lit
float ReadShadowMap(in vec3 fragPos, in vec3 normal, in float diffuseFactor, in mat4 lightMatrix)
{
    if (!ShadowMapEnabled)
        return 1.0;

    float maxBias = 0.04;
    float minBias = 0.001;
    vec3 fragCoord = XRENGINE_ProjectShadowCoord(lightMatrix, fragPos);

    // Outside shadow map bounds: treat as fully lit
    if (!XRENGINE_ShadowCoordInBounds(fragCoord))
        return 1.0;

    float bias = max(maxBias * (1.0 - max(diffuseFactor, 0.0)), minBias);
    return XRENGINE_SampleShadowMapSimple(ShadowMap, fragCoord, bias);
}

vec3 CalcColor(BaseLight light, vec3 lightDirection, vec3 normal, vec3 fragPos, vec3 albedo, float spec, float ambientOcclusion, bool useShadow)
{
    vec3 AmbientColor = vec3(light.Color * light.AmbientIntensity);
    vec3 DiffuseColor = vec3(0.0);
    vec3 SpecularColor = vec3(0.0);

    float DiffuseFactor = dot(normal, -lightDirection);
    if (DiffuseFactor > 0.0)
    {
        DiffuseColor = light.Color * light.DiffuseIntensity * albedo * DiffuseFactor;

        vec3 posToEye = normalize(CameraPosition - fragPos);
        vec3 reflectDir = reflect(lightDirection, normal);
        float SpecularFactor = dot(posToEye, reflectDir);
        if (SpecularFactor > 0.0)
        {
            SpecularColor = light.Color * spec * pow(SpecularFactor, 64.0);
        }
    }

    float shadow = useShadow ? ReadShadowMap(fragPos, normal, DiffuseFactor, light.WorldToLightSpaceProjMatrix) : 1.0;
    return (AmbientColor + (DiffuseColor + SpecularColor) * shadow) * ambientOcclusion;
}

vec3 CalcDirLight(DirLight light, vec3 normal, vec3 fragPos, vec3 albedo, float spec, float ambientOcclusion, bool useShadow)
{
    return CalcColor(light.Base, light.Direction, normal, fragPos, albedo, spec, ambientOcclusion, useShadow);
}

vec3 CalcPointLight(PointLight light, vec3 normal, vec3 fragPos, vec3 albedo, float spec, float ambientOcclusion)
{
    vec3 lightToPos = fragPos - light.Position;
    return XRENGINE_Attenuate(length(lightToPos), light.Radius) * CalcColor(light.Base, normalize(lightToPos), normal, fragPos, albedo, spec, ambientOcclusion, false);
} 

vec3 CalcSpotLight(SpotLight light, vec3 normal, vec3 fragPos, vec3 albedo, float spec, float ambientOcclusion)
{
    //if (light.OuterCutoff <= 1.5707) //~90 degrees in radians
    {
        vec3 lightToPos = normalize(fragPos - light.Base.Position);
        float clampedCosine = max(0.0, dot(lightToPos, normalize(light.Direction)));
        float spotEffect = smoothstep(light.OuterCutoff, light.InnerCutoff, clampedCosine);
	    //if (clampedCosine >= light.OuterCutoff)
        {
            vec3 lightToPos = fragPos - light.Base.Position;
            float spotAttn = pow(clampedCosine, light.Exponent);
            float distAttn = XRENGINE_Attenuate(length(lightToPos) / light.Base.Brightness, light.Base.Radius);
            vec3 color = CalcColor(light.Base.Base, normalize(lightToPos), normal, fragPos, albedo, spec, ambientOcclusion, false);
            return spotEffect * spotAttn * distAttn * color;
        }
    }
    return vec3(0.0);
}

vec3 CalcTotalLight(vec3 normal, vec3 fragPosWS, vec3 albedoColor, float specularIntensity, float ambientOcclusion)
{
    vec3 totalLight = GlobalAmbient;

    for (int i = 0; i < DirLightCount; ++i)
    {
        bool useShadow = (i == 0);
        totalLight += CalcDirLight(DirectionalLights[i], normal, fragPosWS, albedoColor, specularIntensity, ambientOcclusion, useShadow);
    }

    for (int i = 0; i < PointLightCount; ++i)
        totalLight += CalcPointLight(PointLights[i], normal, fragPosWS, albedoColor, specularIntensity, ambientOcclusion);

    for (int i = 0; i < SpotLightCount; ++i)
        totalLight += CalcSpotLight(SpotLights[i], normal, fragPosWS, albedoColor, specularIntensity, ambientOcclusion);

    return totalLight;
}
