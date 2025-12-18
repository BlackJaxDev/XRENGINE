// PBR Specular Functions Snippet
// Cook-Torrance BRDF functions for physically-based rendering

#ifndef PI
#define PI 3.14159265359
#endif

#ifndef INV_PI
#define INV_PI 0.31831
#endif

// ============================================================================
// Normal Distribution Functions (D term)
// ============================================================================

// Trowbridge-Reitz GGX
float XRENGINE_D_GGX(float NoH, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float NoH2 = NoH * NoH;
    float num = a2;
    float denom = (NoH2 * (a2 - 1.0) + 1.0);
    denom = PI * denom * denom;
    return num / denom;
}

// Blinn-Phong distribution
float XRENGINE_D_Blinn(float NoH, float roughness)
{
    float m = roughness * roughness;
    float m2 = m * m;
    float n = 2.0 / m2 - 2.0;
    return (n + 2.0) / (2.0 * PI) * pow(NoH, n);
}

// Beckmann distribution
float XRENGINE_D_Beckmann(float NoH, float roughness)
{
    float m = roughness * roughness;
    float m2 = m * m;
    float NoH2 = NoH * NoH;
    return exp((NoH2 - 1.0) / (m2 * NoH2)) / (PI * m2 * NoH2 * NoH2);
}

// ============================================================================
// Geometry/Visibility Functions (G term)
// ============================================================================

// Schlick-GGX
float XRENGINE_G_SchlickGGX(float NdotV, float roughness)
{
    float r = (roughness + 1.0);
    float k = (r * r) / 8.0;
    float num = NdotV;
    float denom = NdotV * (1.0 - k) + k;
    return num / denom;
}

// Smith's method using Schlick-GGX
float XRENGINE_G_Smith(float NoV, float NoL, float roughness)
{
    float ggx1 = XRENGINE_G_SchlickGGX(NoV, roughness);
    float ggx2 = XRENGINE_G_SchlickGGX(NoL, roughness);
    return ggx1 * ggx2;
}

// Smith's visibility term (combined with denominator)
float XRENGINE_G_SmithCorrelated(float NoV, float NoL, float roughness)
{
    float k = roughness * roughness * 0.5;
    float V = NoV * (1.0 - k) + k;
    float L = NoL * (1.0 - k) + k;
    return 0.25 / (V * L);
}

// ============================================================================
// Fresnel Functions (F term)
// ============================================================================

// Schlick's approximation
vec3 XRENGINE_F_Schlick(float VoH, vec3 F0)
{
    return F0 + (1.0 - F0) * pow(clamp(1.0 - VoH, 0.0, 1.0), 5.0);
}

// Schlick with roughness for IBL
vec3 XRENGINE_F_SchlickRoughness(float VoH, vec3 F0, float roughness)
{
    return F0 + (max(vec3(1.0 - roughness), F0) - F0) * pow(clamp(1.0 - VoH, 0.0, 1.0), 5.0);
}

// Spherical Gaussian approximation (faster)
vec3 XRENGINE_F_SchlickFast(float VoH, vec3 F0)
{
    float p = exp2((-5.55473 * VoH - 6.98316) * VoH);
    return F0 + (1.0 - F0) * p;
}

// ============================================================================
// Complete BRDF Terms
// ============================================================================

// Cook-Torrance specular BRDF
vec3 XRENGINE_CookTorranceSpecular(float D, float G, vec3 F, float NoV, float NoL)
{
    vec3 num = D * G * F;
    float denom = 4.0 * NoV * NoL + 0.0001;
    return num / denom;
}

// Simple Phong diffuse (Lambertian)
float XRENGINE_PhongDiffuse()
{
    return INV_PI;
}

// Phong specular with normalization
vec3 XRENGINE_PhongSpecular(vec3 V, vec3 L, vec3 N, vec3 specular, float roughness)
{
    vec3 R = reflect(-L, N);
    float spec = max(0.0, dot(V, R));
    float k = 1.999 / (roughness * roughness);
    return min(1.0, 3.0 * 0.0398 * k) * pow(spec, min(10000.0, k)) * specular;
}

// Blinn-Phong specular with normalization
vec3 XRENGINE_BlinnSpecular(float NoH, vec3 specular, float roughness)
{
    float k = 1.999 / (roughness * roughness);
    return min(1.0, 3.0 * 0.0398 * k) * pow(NoH, min(10000.0, k)) * specular;
}

// ============================================================================
// F0 Calculation
// ============================================================================

// Calculate F0 from IOR
vec3 XRENGINE_F0FromIOR(float ior)
{
    float f0 = (1.0 - ior) / (1.0 + ior);
    return vec3(f0 * f0);
}

// Calculate F0 for metallic workflow
vec3 XRENGINE_F0Metallic(vec3 albedo, float metallic)
{
    return mix(vec3(0.04), albedo, metallic);
}

// ============================================================================
// Full PBR Lighting Calculation
// ============================================================================

// Calculate PBR lighting for a single light
vec3 XRENGINE_CalculatePBRDirectLight(
    vec3 N,
    vec3 V,
    vec3 L,
    vec3 lightColor,
    vec3 albedo,
    float roughness,
    float metallic,
    float attenuation)
{
    vec3 H = normalize(V + L);
    
    float NoV = max(dot(N, V), 0.001);
    float NoL = max(dot(N, L), 0.0);
    float NoH = max(dot(N, H), 0.001);
    float VoH = max(dot(V, H), 0.001);
    
    if (NoL <= 0.0)
        return vec3(0.0);
    
    vec3 F0 = XRENGINE_F0Metallic(albedo, metallic);
    
    float D = XRENGINE_D_GGX(NoH, roughness);
    float G = XRENGINE_G_Smith(NoV, NoL, roughness);
    vec3 F = XRENGINE_F_Schlick(VoH, F0);
    
    vec3 kS = F;
    vec3 kD = (vec3(1.0) - kS) * (1.0 - metallic);
    
    vec3 specular = XRENGINE_CookTorranceSpecular(D, G, F, NoV, NoL);
    vec3 diffuse = kD * albedo * INV_PI;
    
    return (diffuse + specular) * lightColor * attenuation * NoL;
}

// ============================================================================
// IBL Helper Functions
// ============================================================================

// Hammersley sequence for importance sampling
vec2 XRENGINE_Hammersley(uint i, uint N)
{
    uint bits = i;
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    float rdi = float(bits) * 2.3283064365386963e-10;
    return vec2(float(i) / float(N), rdi);
}

// Importance sample GGX
vec3 XRENGINE_ImportanceSampleGGX(vec2 Xi, float roughness, vec3 N)
{
    float a = roughness * roughness;
    
    float phi = 2.0 * PI * Xi.x;
    float cosTheta = sqrt((1.0 - Xi.y) / (1.0 + (a * a - 1.0) * Xi.y));
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);
    
    vec3 H = vec3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta);
    
    vec3 up = abs(N.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
    vec3 tangentX = normalize(cross(up, N));
    vec3 tangentY = cross(N, tangentX);
    
    return tangentX * H.x + tangentY * H.y + N * H.z;
}
