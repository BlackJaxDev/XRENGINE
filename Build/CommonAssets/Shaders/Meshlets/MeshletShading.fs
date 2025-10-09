#version 460 core

// Input vertex attributes
layout(location = 0) in vec3 in_worldPos;
layout(location = 1) in vec3 in_normal;
layout(location = 2) in vec2 in_texCoord;
layout(location = 3) in vec4 in_tangent;
layout(location = 4) flat in uint in_materialID;

// Output
layout(location = 0) out vec4 out_color;

// Material data
struct Material
{
    vec4 albedo;
    float metallic;
    float roughness;
    float ao;
    uint diffuseTextureID;
    uint normalTextureID;
    uint metallicRoughnessTextureID;
};

layout(std430, binding = 6) buffer MaterialBuffer
{
    Material materials[];
};

// Reduce count to a safer value unless bindless is used
uniform sampler2D textures[32];

// Lighting uniforms
uniform vec3 cameraPosition;
uniform vec3 lightDirection;
uniform vec3 lightColor;
uniform float lightIntensity;

const float PI = 3.14159265359;

// PBR calculation functions
vec3 fresnelSchlick(float cosTheta, vec3 F0)
{
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

float distributionGGX(vec3 N, vec3 H, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float NdotH2 = NdotH * NdotH;
    
    float num = a2;
    float denom = (NdotH2 * (a2 - 1.0) + 1.0);
    denom = PI * denom * denom;
    
    return num / max(denom, 1e-5);
}

float geometrySchlickGGX(float NdotV, float roughness)
{
    float r = (roughness + 1.0);
    float k = (r * r) / 8.0;
    
    float num = NdotV;
    float denom = NdotV * (1.0 - k) + k;
    
    return num / max(denom, 1e-5);
}

float geometrySmith(vec3 N, vec3 V, vec3 L, float roughness)
{
    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    float ggx2 = geometrySchlickGGX(NdotV, roughness);
    float ggx1 = geometrySchlickGGX(NdotL, roughness);
    
    return ggx1 * ggx2;
}

void main()
{
    // Get material
    Material material = materials[in_materialID];
    
    // Sample textures
    vec4 albedo = material.albedo;
    if (material.diffuseTextureID < 32u)
        albedo *= texture(textures[material.diffuseTextureID], in_texCoord);
    
    vec3 normal = normalize(in_normal);
    if (material.normalTextureID < 32u)
    {
        // Sample normal map and transform to world space
        vec3 normalMap = texture(textures[material.normalTextureID], in_texCoord).rgb * 2.0 - 1.0;
        
        vec3 T = normalize(in_tangent.xyz);
        vec3 N = normal;
        vec3 B = normalize(cross(N, T)) * in_tangent.w;
        mat3 TBN = mat3(T, B, N);
        
        normal = normalize(TBN * normalMap);
    }
    
    float metallic = clamp(material.metallic, 0.0, 1.0);
    float roughness = clamp(material.roughness, 0.04, 1.0);
    float ao = clamp(material.ao, 0.0, 1.0);
    
    if (material.metallicRoughnessTextureID < 32u)
    {
        vec3 mrSample = texture(textures[material.metallicRoughnessTextureID], in_texCoord).rgb;
        metallic *= mrSample.b;
        roughness *= mrSample.g;
        ao *= mrSample.r;
    }
    
    // PBR lighting calculation
    vec3 V = normalize(cameraPosition - in_worldPos);
    vec3 L = normalize(-lightDirection);
    vec3 H = normalize(V + L);
    
    vec3 F0 = vec3(0.04);
    F0 = mix(F0, albedo.rgb, metallic);
    
    vec3 F = fresnelSchlick(max(dot(H, V), 0.0), F0);
    float NDF = distributionGGX(normal, H, roughness);
    float G = geometrySmith(normal, V, L, roughness);
    
    vec3 numerator = NDF * G * F;
    float denominator = 4.0 * max(dot(normal, V), 0.0) * max(dot(normal, L), 0.0) + 1e-5;
    vec3 specular = numerator / denominator;
    
    vec3 kS = F;
    vec3 kD = (vec3(1.0) - kS) * (1.0 - metallic);
    
    float NdotL = max(dot(normal, L), 0.0);
    vec3 radiance = lightColor * lightIntensity;
    
    vec3 Lo = (kD * albedo.rgb / PI + specular) * radiance * NdotL;
    
    // Add ambient
    vec3 ambient = vec3(0.03) * albedo.rgb * ao;
    vec3 color = ambient + Lo;
    
    // Tone mapping and gamma correction
    color = color / (color + vec3(1.0));
    color = pow(color, vec3(1.0/2.2));
    
    out_color = vec4(color, albedo.a);
}
