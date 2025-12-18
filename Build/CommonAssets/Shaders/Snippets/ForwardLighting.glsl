// Forward Lighting Snippet - Basic Blinn-Phong with Forward+ support
// Requires: CameraPosition (vec3), normal (vec3), fragPos (vec3)

struct BaseLight
{
    vec3 Color;
    float DiffuseIntensity;
    float AmbientIntensity;
    mat4 WorldToLightSpaceProjMatrix;
    sampler2D ShadowMap;
};

struct DirLight
{
    BaseLight Base;
    vec3 Direction;
};

struct PointLight
{
    BaseLight Base;
    vec3 Position;
    float Radius;
    float Brightness;
};

struct SpotLight
{
    PointLight Base;
    vec3 Direction;
    float InnerCutoff;
    float OuterCutoff;
    float Exponent;
};

uniform vec3 GlobalAmbient;

uniform int DirLightCount; 
uniform DirLight DirectionalLights[2];

uniform int SpotLightCount;
uniform SpotLight SpotLights[16];

uniform int PointLightCount;
uniform PointLight PointLights[16];

// Forward+ tiled light culling uniforms
uniform bool ForwardPlusEnabled;
uniform vec2 ForwardPlusScreenSize;
uniform int ForwardPlusTileSize;
uniform int ForwardPlusMaxLightsPerTile;

struct ForwardPlusLocalLight
{
    vec4 PositionWS;
    vec4 DirectionWS_Exponent;
    vec4 Color_Type;     // rgb=color, w=type (0=point, 1=spot)
    vec4 Params;         // x=radius, y=brightness, z=diffuseIntensity
    vec4 SpotAngles;     // x=innerCutoff, y=outerCutoff
};

layout(std430, binding = 20) readonly buffer ForwardPlusLocalLightsBuffer
{
    ForwardPlusLocalLight ForwardPlusLocalLights[];
};

layout(std430, binding = 21) readonly buffer ForwardPlusVisibleIndicesBuffer
{
    int ForwardPlusVisibleIndices[];
};

float XRENGINE_Attenuate(float dist, float radius)
{
    return pow(clamp(1.0 - pow(dist / radius, 4.0), 0.0, 1.0), 2.0) / (dist * dist + 1.0);
}

float XRENGINE_ReadShadowMap(vec3 fragPos, vec3 normal, float diffuseFactor, BaseLight light)
{
    float maxBias = 0.04;
    float minBias = 0.001;

    vec4 fragPosLightSpace = light.WorldToLightSpaceProjMatrix * vec4(fragPos, 1.0);
    vec3 fragCoord = fragPosLightSpace.xyz / fragPosLightSpace.w;
    fragCoord = fragCoord * 0.5 + 0.5;
    float bias = max(maxBias * -diffuseFactor, minBias);

    float depth = texture(light.ShadowMap, fragCoord.xy).r;
    return (fragCoord.z - bias) > depth ? 0.0 : 1.0;
}

vec3 XRENGINE_CalcLightColor(BaseLight light, vec3 lightDirection, vec3 normal, vec3 fragPos, vec3 albedo, float spec, float ambientOcclusion)
{
    vec3 AmbientColor = light.Color * light.AmbientIntensity;
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
            SpecularColor = light.Color * spec * pow(SpecularFactor, 64.0);
    }

    float shadow = XRENGINE_ReadShadowMap(fragPos, normal, DiffuseFactor, light);
    return (AmbientColor + (DiffuseColor + SpecularColor) * shadow) * ambientOcclusion;
}

vec3 XRENGINE_CalcDirLight(DirLight light, vec3 normal, vec3 fragPos, vec3 albedo, float spec, float ambientOcclusion)
{
    return XRENGINE_CalcLightColor(light.Base, light.Direction, normal, fragPos, albedo, spec, ambientOcclusion);
}

vec3 XRENGINE_CalcPointLight(PointLight light, vec3 normal, vec3 fragPos, vec3 albedo, float spec, float ambientOcclusion)
{
    vec3 lightToPos = fragPos - light.Position;
    return XRENGINE_Attenuate(length(lightToPos), light.Radius) * XRENGINE_CalcLightColor(light.Base, normalize(lightToPos), normal, fragPos, albedo, spec, ambientOcclusion);
}

vec3 XRENGINE_CalcSpotLight(SpotLight light, vec3 normal, vec3 fragPos, vec3 albedo, float spec, float ambientOcclusion)
{
    vec3 lightToPos = normalize(fragPos - light.Base.Position);
    float clampedCosine = max(0.0, dot(lightToPos, normalize(light.Direction)));
    float spotEffect = smoothstep(light.OuterCutoff, light.InnerCutoff, clampedCosine);

    vec3 lightToPosUn = fragPos - light.Base.Position;
    float spotAttn = pow(clampedCosine, light.Exponent);
    float distAttn = XRENGINE_Attenuate(length(lightToPosUn) / light.Base.Brightness, light.Base.Radius);
    vec3 color = XRENGINE_CalcLightColor(light.Base.Base, normalize(lightToPosUn), normal, fragPos, albedo, spec, ambientOcclusion);
    return spotEffect * spotAttn * distAttn * color;
}

vec3 XRENGINE_CalcForwardPlusColor(vec3 lightColor, float diffuseIntensity, vec3 lightDirection, vec3 normal, vec3 fragPos, vec3 albedo, float spec, float ambientOcclusion)
{
    vec3 DiffuseColor = vec3(0.0);
    vec3 SpecularColor = vec3(0.0);

    float DiffuseFactor = dot(normal, -lightDirection);
    if (DiffuseFactor > 0.0)
    {
        DiffuseColor = lightColor * diffuseIntensity * albedo * DiffuseFactor;

        vec3 posToEye = normalize(CameraPosition - fragPos);
        vec3 reflectDir = reflect(lightDirection, normal);
        float SpecularFactor = dot(posToEye, reflectDir);
        if (SpecularFactor > 0.0)
            SpecularColor = lightColor * spec * pow(SpecularFactor, 64.0);
    }

    return (DiffuseColor + SpecularColor) * ambientOcclusion;
}

vec3 XRENGINE_CalcForwardPlusPointLight(ForwardPlusLocalLight light, vec3 normal, vec3 fragPos, vec3 albedo, float spec, float ambientOcclusion)
{
    vec3 lightToPos = fragPos - light.PositionWS.xyz;
    float dist = length(lightToPos);
    float attn = XRENGINE_Attenuate(dist, light.Params.x);
    return attn * XRENGINE_CalcForwardPlusColor(light.Color_Type.xyz, light.Params.z, normalize(lightToPos), normal, fragPos, albedo, spec, ambientOcclusion);
}

vec3 XRENGINE_CalcForwardPlusSpotLight(ForwardPlusLocalLight light, vec3 normal, vec3 fragPos, vec3 albedo, float spec, float ambientOcclusion)
{
    vec3 lightDir = normalize(light.DirectionWS_Exponent.xyz);
    vec3 lightToPosN = normalize(fragPos - light.PositionWS.xyz);
    float clampedCosine = max(0.0, dot(lightToPosN, lightDir));
    float spotEffect = smoothstep(light.SpotAngles.y, light.SpotAngles.x, clampedCosine);

    vec3 lightToPos = fragPos - light.PositionWS.xyz;
    float spotAttn = pow(clampedCosine, light.DirectionWS_Exponent.w);
    float distAttn = XRENGINE_Attenuate(length(lightToPos) / max(light.Params.y, 0.0001), light.Params.x);

    return spotEffect * spotAttn * distAttn * XRENGINE_CalcForwardPlusColor(light.Color_Type.xyz, light.Params.z, normalize(lightToPos), normal, fragPos, albedo, spec, ambientOcclusion);
}

// Main lighting calculation function
// Call this from your fragment shader main() with your surface parameters
vec3 XRENGINE_CalculateForwardLighting(vec3 normal, vec3 fragPos, vec3 albedo, float specularIntensity, float ambientOcclusion)
{
    vec3 totalLight = GlobalAmbient;

    // Directional lights (always processed)
    for (int i = 0; i < DirLightCount; ++i)
        totalLight += XRENGINE_CalcDirLight(DirectionalLights[i], normal, fragPos, albedo, specularIntensity, ambientOcclusion);

    // Local lights: use Forward+ if available, otherwise brute-force
    if (ForwardPlusEnabled)
    {
        ivec2 tileCoord = ivec2(gl_FragCoord.xy) / ForwardPlusTileSize;
        int tileCountX = (int(ForwardPlusScreenSize.x) + ForwardPlusTileSize - 1) / ForwardPlusTileSize;
        int tileIndex = tileCoord.y * tileCountX + tileCoord.x;
        int baseIndex = tileIndex * ForwardPlusMaxLightsPerTile;

        for (int o = 0; o < ForwardPlusMaxLightsPerTile; ++o)
        {
            int lightIndex = ForwardPlusVisibleIndices[baseIndex + o];
            if (lightIndex < 0)
                break;

            ForwardPlusLocalLight l = ForwardPlusLocalLights[lightIndex];
            totalLight += (l.Color_Type.w < 0.5)
                ? XRENGINE_CalcForwardPlusPointLight(l, normal, fragPos, albedo, specularIntensity, ambientOcclusion)
                : XRENGINE_CalcForwardPlusSpotLight(l, normal, fragPos, albedo, specularIntensity, ambientOcclusion);
        }
    }
    else
    {
        for (int i = 0; i < PointLightCount; ++i)
            totalLight += XRENGINE_CalcPointLight(PointLights[i], normal, fragPos, albedo, specularIntensity, ambientOcclusion);

        for (int i = 0; i < SpotLightCount; ++i)
            totalLight += XRENGINE_CalcSpotLight(SpotLights[i], normal, fragPos, albedo, specularIntensity, ambientOcclusion);
    }

    return totalLight;
}
