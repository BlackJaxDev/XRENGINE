// Forward Lighting PBR Snippet
// Physically-based rendering with Forward+ support
// Include ForwardLighting snippet first for light structures

#ifndef PI
#define PI 3.14159265359
#endif

float XRENGINE_DistributionGGX(vec3 N, vec3 H, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float NdotH2 = NdotH * NdotH;

    float num = a2;
    float denom = (NdotH2 * (a2 - 1.0) + 1.0);
    denom = PI * denom * denom;

    return num / denom;
}

float XRENGINE_GeometrySchlickGGX(float NdotV, float roughness)
{
    float r = (roughness + 1.0);
    float k = (r * r) / 8.0;

    float num = NdotV;
    float denom = NdotV * (1.0 - k) + k;

    return num / denom;
}

float XRENGINE_GeometrySmith(vec3 N, vec3 V, vec3 L, float roughness)
{
    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    float ggx2 = XRENGINE_GeometrySchlickGGX(NdotV, roughness);
    float ggx1 = XRENGINE_GeometrySchlickGGX(NdotL, roughness);

    return ggx1 * ggx2;
}

vec3 XRENGINE_FresnelSchlick(float cosTheta, vec3 F0)
{
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

vec3 XRENGINE_FresnelSchlickRoughness(float cosTheta, vec3 F0, float roughness)
{
    return F0 + (max(vec3(1.0 - roughness), F0) - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

vec3 XRENGINE_CalculatePBRLight(vec3 lightColor, vec3 L, vec3 V, vec3 N, vec3 albedo, float metallic, float roughness, vec3 F0, float attenuation)
{
    vec3 H = normalize(V + L);
    vec3 radiance = lightColor * attenuation;

    float NDF = XRENGINE_DistributionGGX(N, H, roughness);
    float G = XRENGINE_GeometrySmith(N, V, L, roughness);
    vec3 F = XRENGINE_FresnelSchlick(max(dot(H, V), 0.0), F0);

    vec3 kS = F;
    vec3 kD = vec3(1.0) - kS;
    kD *= 1.0 - metallic;

    vec3 numerator = NDF * G * F;
    float denominator = 4.0 * max(dot(N, V), 0.0) * max(dot(N, L), 0.0) + 0.0001;
    vec3 specular = numerator / denominator;

    float NdotL = max(dot(N, L), 0.0);

    return (kD * albedo / PI + specular) * radiance * NdotL;
}
