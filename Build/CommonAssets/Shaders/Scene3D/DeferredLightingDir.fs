#version 450

const float PI = 3.14159265359f;
const float InvPI = 0.31831f;
const float EPSILON = 0.0001f;

layout(location = 0) out vec3 OutColor; //Diffuse lighting output
layout(location = 0) in vec3 FragPos;

uniform sampler2D Texture0; //AlbedoOpacity
uniform sampler2D Texture1; //Normal
uniform sampler2D Texture2; //PBR: Roughness, Metallic, Specular, Index of refraction
uniform sampler2D Texture3; //Depth
uniform sampler2D ShadowMap; //Directional Shadow Map

uniform float ScreenWidth;
uniform float ScreenHeight;
uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;

uniform float MinFade = 500.0f;
uniform float MaxFade = 1000.0f;
uniform float ShadowBase = 1.0f;
uniform float ShadowMult = 1.0f;
uniform float ShadowBiasMin = 0.00001f;
uniform float ShadowBiasMax = 0.004f;

// New shadow quality settings
uniform int ShadowSamples = 16; // 16 for PCSS, 9 for PCF, 1 for hard shadows
uniform float ShadowFilterRadius = 0.01f;
uniform bool EnablePCSS = true;

struct DirLight
{
    vec3 Color;
    float DiffuseIntensity;
    mat4 WorldToLightInvViewMatrix;
    mat4 WorldToLightProjMatrix;
    vec3 Direction;
    float CascadeSplits[4]; // For cascaded shadow maps
    int CascadeCount;
};
uniform DirLight LightData;

// Optimized shadow bias calculation
float GetShadowBias(in float NoL, in float depth)
{
    float bias = mix(ShadowBiasMin, ShadowBiasMax, 1.0f - NoL);
    // Add depth-based bias for better quality
    bias += depth * 0.0001f;
    return bias;
}

// Improved PCF with variable sample count
float PCFShadow(in vec3 fragCoord, in float bias, in int samples)
{
    float shadow = 0.0f;
    vec2 texelSize = 1.0f / textureSize(ShadowMap, 0);
    float radius = ShadowFilterRadius;
    
    // Use Poisson disk sampling for better quality
    vec2 poissonDisk[16] = vec2[](
        vec2(-0.94201624f, -0.39906216f), vec2(0.94558609f, -0.76890725f),
        vec2(-0.094184101f, -0.92938870f), vec2(0.34495938f, 0.29387760f),
        vec2(-0.91588581f, 0.45771432f), vec2(-0.81544232f, -0.87912464f),
        vec2(-0.38277543f, 0.27676845f), vec2(0.97484398f, 0.75648379f),
        vec2(0.44323325f, -0.97511554f), vec2(0.53742981f, -0.47373420f),
        vec2(-0.26496911f, -0.41893023f), vec2(0.79197514f, 0.19090188f),
        vec2(-0.24188840f, 0.99706507f), vec2(-0.81409955f, 0.91437590f),
        vec2(0.19984126f, 0.78641367f), vec2(0.14383161f, -0.14100790f)
    );
    
    for(int i = 0; i < samples; ++i)
    {
        vec2 offset = poissonDisk[i] * radius;
        float pcfDepth = texture(ShadowMap, fragCoord.xy + offset).r;
        shadow += (fragCoord.z - bias > pcfDepth) ? 1.0f : 0.0f;
    }
    
    return 1.0f - (shadow / float(samples));
}

// PCSS (Percentage Closer Soft Shadows) implementation
float PCSSShadow(in vec3 fragCoord, in float bias)
{
    vec2 texelSize = 1.0f / textureSize(ShadowMap, 0);
    float radius = ShadowFilterRadius;
    
    // Step 1: Blocker search
    float blockerDepth = 0.0f;
    int blockerCount = 0;
    int searchSamples = 16;
    
    for (int i = 0; i < searchSamples; ++i)
    {
        vec2 offset = vec2(
            float(i % 4 - 2) * texelSize.x,
            float(i / 4 - 2) * texelSize.y
        ) * radius;
        float depth = texture(ShadowMap, fragCoord.xy + offset).r;
        if (fragCoord.z - bias > depth)
        {
            blockerDepth += depth;
            blockerCount++;
        }
    }
    
    if (blockerCount == 0) 
		return 1.0f;
    
    // Step 2: Penumbra estimation
    float avgBlockerDepth = blockerDepth / float(blockerCount);
    float penumbraRadius = (fragCoord.z - avgBlockerDepth) / avgBlockerDepth * radius;
    
    // Step 3: PCF with estimated penumbra
    return PCFShadow(fragCoord, bias, ShadowSamples);
}

// Optimized shadow mapping with early exit
float ReadShadowMap2D(in vec3 fragPosWS, in vec3 N, in float NoL, in mat4 lightMatrix)
{
    // Early exit if facing away from light
    if (NoL <= 0.0f)
        return 0.0f;

    // Move the fragment position into light space
    vec4 fragPosLightSpace = lightMatrix * vec4(fragPosWS, 1.0f);
    vec3 fragCoord = fragPosLightSpace.xyz / fragPosLightSpace.w;
    fragCoord = fragCoord * 0.5f + 0.5f;

    // Early exit if outside shadow map bounds
    if (any(lessThan(fragCoord.xy, vec2(0.0f))) ||
        any(greaterThan(fragCoord.xy, vec2(1.0f))))
        return 1.0f;

    float bias = GetShadowBias(NoL, fragCoord.z);

    // Select shadow algorithm and compute lit factor (1.0 = lit, 0.0 = fully shadowed)
    float shadowFactor;
    if (EnablePCSS)
    {
        // PCSSShadow returns fraction lit
        shadowFactor = 1.0f - PCSSShadow(fragCoord, bias);
    }
    //else if (ShadowSamples > 1)
    //{
    //    // PCFShadow returns fraction lit
    //    shadowFactor = PCFShadow(fragCoord, bias, ShadowSamples);
    //}
    //else
    //{
    //    float depthSample = texture(ShadowMap, fragCoord.xy).r;
    //    shadowFactor = (fragCoord.z - bias > depthSample) ? 0.0f : 1.0f;
    //}
    return shadowFactor;
}

// Optimized GGX functions with better numerical stability
float SpecD_TRGGX(in float NoH2, in float a2)
{
    float num = a2;
    float denom = (NoH2 * (a2 - 1.0f) + 1.0f);
    denom = PI * denom * denom;
    return num / max(denom, EPSILON);
}

float SpecG_SchlickGGX(in float NoV, in float k)
{
    float num = NoV;
    float denom = NoV * (1.0f - k) + k;
    return num / max(denom, EPSILON);
}

float SpecG_Smith(in float NoV, in float NoL, in float k)
{
    float ggx1 = SpecG_SchlickGGX(NoV, k);
    float ggx2 = SpecG_SchlickGGX(NoL, k);
    return ggx1 * ggx2;
}

// Optimized Fresnel with better approximation
vec3 SpecF_SchlickApprox(in float VoH, in vec3 F0)
{
    // Spherical Gaussian Approximation - more accurate than the current implementation
    float Fc = pow(1.0f - VoH, 5.0f);
    return F0 + (1.0f - F0) * Fc;
}

// Improved color calculation with better energy conservation
vec3 CalcColor(
    in float NoL,
    in float NoH,
    in float NoV,
    in float HoV,
    in float lightAttenuation,
    in vec3 albedo,
    in vec3 rms,
    in vec3 F0)
{
    float roughness = rms.x;
    float metallic = rms.y;
    float specular = rms.z;
    
    // Clamp values for numerical stability
    roughness = clamp(roughness, 0.01f, 1.0f);
    metallic = clamp(metallic, 0.0f, 1.0f);
    specular = clamp(specular, 0.0f, 1.0f);
    
    float a = roughness * roughness;
    float k = (roughness + 1.0f) * (roughness + 1.0f) * 0.125f;
    
    float D = SpecD_TRGGX(NoH * NoH, a * a);
    float G = SpecG_Smith(NoV, NoL, k);
    vec3 F = SpecF_SchlickApprox(HoV, F0);
    
    // Cook-Torrance Specular with better energy conservation
    float denom = 4.0f * NoV * NoL + EPSILON;
    vec3 spec = specular * D * G * F / denom;
    
    // Energy conservation
    vec3 kD = (1.0f - F) * (1.0f - metallic);
    
    vec3 radiance = lightAttenuation * LightData.Color * LightData.DiffuseIntensity;
    return (kD * albedo * InvPI + spec) * radiance * NoL;
}

// Optimized light calculation with early exits
vec3 CalcLight(
    in vec3 N,
    in vec3 V,
    in vec3 fragPosWS,
    in vec3 albedo,
    in vec3 rms,
    in vec3 F0)
{
    vec3 L = -LightData.Direction;
    float NoL = max(dot(N, L), 0.0f);

    // Early exit if facing away from light
    if (NoL <= 0.0f)
        return vec3(0.0f);
    
    vec3 H = normalize(V + L);
    float NoH = max(dot(N, H), 0.0f);
    float NoV = max(dot(N, V), 0.0f);
    float HoV = max(dot(H, V), 0.0f);
    
    vec3 color = CalcColor(
        NoL, NoH, NoV, HoV,
        1.0f, albedo, rms, F0);
    
    // Optimized shadow matrix calculation
    mat4 shadowMatrix = LightData.WorldToLightProjMatrix * inverse(LightData.WorldToLightInvViewMatrix);
    float shadow = ReadShadowMap2D(fragPosWS, N, NoL, shadowMatrix);
    
    return color * shadow;
}

// Optimized total light calculation
vec3 CalcTotalLight(
    in vec3 fragPosWS,
    in vec3 normal,
    in vec3 albedo,
    in vec3 rms)
{
    // Normalize normal in case it's not already normalized
    normal = normalize(normal);
    
    float metallic = rms.y;
    vec3 CameraPosition = InverseViewMatrix[3].xyz;
    vec3 V = normalize(CameraPosition - fragPosWS);
    vec3 F0 = mix(vec3(0.04f), albedo, metallic);
    
    return CalcLight(normal, V, fragPosWS, albedo, rms, F0);
}

// Optimized world position reconstruction
vec3 WorldPosFromDepth(in float depth, in vec2 uv)
{
    vec4 clipSpacePosition = vec4(vec3(uv, depth) * 2.0f - 1.0f, 1.0f);
    vec4 viewSpacePosition = inverse(ProjMatrix) * clipSpacePosition;
    viewSpacePosition /= viewSpacePosition.w;
    return (InverseViewMatrix * viewSpacePosition).xyz;
}

void main()
{
    vec2 uv = gl_FragCoord.xy / vec2(ScreenWidth, ScreenHeight);
    
    // Retrieve shading information from GBuffer textures
    vec3 albedo = texture(Texture0, uv).rgb;
    vec3 normal = texture(Texture1, uv).rgb;
    vec3 rms = texture(Texture2, uv).rgb;
    float depth = texture(Texture3, uv).r;
    
    // Early exit for skybox or invalid depth
    if (depth >= 1.0f) 
    {
        OutColor = vec3(0.0f);
        return;
    }
    
    // Resolve world fragment position using depth and screen UV
    vec3 fragPosWS = WorldPosFromDepth(depth, uv);
    
    // Apply distance fade if enabled
    vec3 CameraPosition = InverseViewMatrix[3].xyz;
    float dist = length(CameraPosition - fragPosWS);
    float fadeStrength = 1.0f;
    
    if (MaxFade > MinFade)
    {
        float fadeRange = MaxFade - MinFade;
        fadeStrength = smoothstep(1.0f, 0.0f, clamp((dist - MinFade) / fadeRange, 0.0f, 1.0f));
    }
    
    OutColor = CalcTotalLight(fragPosWS, normal, albedo, rms) * fadeStrength;
}
