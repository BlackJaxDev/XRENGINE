// Forward Lighting Snippet - PBR Forward+ lighting with probe-based ambient
// Requires: CameraPosition (vec3), normal (vec3), fragPos (vec3)

#pragma snippet "LightStructs"
#pragma snippet "LightAttenuation"

const float PI = 3.14159265359;
const float MAX_REFLECTION_LOD = 4.0;

uniform vec3 GlobalAmbient;
uniform float Roughness = 0.9;
uniform float Metallic = 0.0;
uniform float Emission = 0.0;
uniform bool AmbientOcclusionMultiBounce;
uniform bool SpecularOcclusionEnabled;
uniform bool ForwardPbrResourcesEnabled;

layout(binding = 6) uniform sampler2D BRDF;
layout(binding = 7) uniform sampler2DArray IrradianceArray;
layout(binding = 8) uniform sampler2DArray PrefilterArray;

layout(std430, binding = 0) readonly buffer LightProbePositions
{
    vec4 ProbePositions[];
};

layout(std430, binding = 1) readonly buffer LightProbeTetrahedra
{
    vec4 TetraIndices[];
};

struct ProbeParam
{
    vec4 InfluenceInner;
    vec4 InfluenceOuter;
    vec4 InfluenceOffsetShape;
    vec4 ProxyCenterEnable;
    vec4 ProxyHalfExtents;
    vec4 ProxyRotation;
};

layout(std430, binding = 2) readonly buffer LightProbeParameters
{
    ProbeParam ProbeParams[];
};

layout(std430, binding = 3) readonly buffer LightProbeGridCells
{
    ivec2 CellOffsetCount[];
};

layout(std430, binding = 4) readonly buffer LightProbeGridIndices
{
    int CellProbeIndices[];
};

uniform int ProbeCount;
uniform int TetraCount;
uniform ivec3 ProbeGridDims;
uniform vec3 ProbeGridOrigin;
uniform float ProbeGridCellSize;
uniform bool UseProbeGrid;

// Primary directional light shadow map (bound separately, not in struct)
// Use explicit binding at unit 15 to avoid collision with material textures (which use 0..N).
// The layout(binding) ensures the sampler defaults to unit 15 even if runtime binding fails.
layout(binding = 15) uniform sampler2D ShadowMap;
uniform bool ShadowMapEnabled;
uniform mat4 PrimaryDirLightWorldToLightInvViewMatrix;
uniform mat4 PrimaryDirLightWorldToLightProjMatrix;
uniform float ShadowBase;
uniform float ShadowMult;
uniform float ShadowBiasMin;
uniform float ShadowBiasMax;

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

vec3 XRENGINE_MultiBounceAO(float ao, vec3 albedo)
{
    vec3 a = 2.0404 * albedo - 0.3324;
    vec3 b = -4.7951 * albedo + 0.6417;
    vec3 c = 2.7552 * albedo + 0.6903;
    return max(vec3(ao), ((a * ao + b) * ao + c) * ao);
}

float XRENGINE_GTSpecularOcclusion(float NoV, float ao, float roughness)
{
    return clamp(pow(NoV + ao, exp2(-16.0 * roughness - 1.0)) - 1.0 + ao, 0.0, 1.0);
}

float XRENGINE_SpecD_TRGGX(float NoH2, float a2)
{
    float denom = (NoH2 * (a2 - 1.0) + 1.0);
    return a2 / (PI * denom * denom);
}

float XRENGINE_SpecG_SchlickGGX(float NoV, float k)
{
    return NoV / (NoV * (1.0 - k) + k);
}

float XRENGINE_SpecG_Smith(float NoV, float NoL, float k)
{
    return XRENGINE_SpecG_SchlickGGX(NoV, k) * XRENGINE_SpecG_SchlickGGX(NoL, k);
}

vec3 XRENGINE_SpecF_SchlickApprox(float VoH, vec3 F0)
{
    float powTerm = exp2((-5.55473 * VoH - 6.98316) * VoH);
    return F0 + (1.0 - F0) * powTerm;
}

vec3 XRENGINE_SpecF_SchlickRoughnessApprox(float VoH, vec3 F0, float roughness)
{
    float powTerm = exp2((-5.55473 * VoH - 6.98316) * VoH);
    return F0 + (max(vec3(1.0 - roughness), F0) - F0) * powTerm;
}

vec2 XRENGINE_EncodeOcta(vec3 dir)
{
    dir = normalize(dir);
    vec3 octDir = vec3(dir.x, dir.z, dir.y);
    octDir /= max(abs(octDir.x) + abs(octDir.y) + abs(octDir.z), 1e-5);

    vec2 uv = octDir.xy;
    if (octDir.z < 0.0)
    {
        vec2 signDir = vec2(octDir.x >= 0.0 ? 1.0 : -1.0, octDir.y >= 0.0 ? 1.0 : -1.0);
        uv = (1.0 - abs(uv.yx)) * signDir;
    }

    return uv * 0.5 + 0.5;
}

vec3 XRENGINE_SampleOctaArray(sampler2DArray tex, vec3 dir, float layer)
{
    return texture(tex, vec3(XRENGINE_EncodeOcta(dir), layer)).rgb;
}

vec3 XRENGINE_SampleOctaArrayLod(sampler2DArray tex, vec3 dir, float layer, float lod)
{
    return textureLod(tex, vec3(XRENGINE_EncodeOcta(dir), layer), lod).rgb;
}

mat3 XRENGINE_QuaternionToMat3(vec4 q)
{
    vec3 q2 = q.xyz + q.xyz;
    vec3 qq = q.xyz * q2;
    float qx2 = q.x * q2.x;
    float qy2 = q.y * q2.y;
    float qz2 = q.z * q2.z;

    vec3 qwq = q.w * q2;
    vec3 m0 = vec3(1.0 - (qy2 + qz2), qq.x + qwq.z, qq.y - qwq.y);
    vec3 m1 = vec3(qq.x - qwq.z, 1.0 - (qx2 + qz2), qq.z + qwq.x);
    vec3 m2 = vec3(qq.y + qwq.y, qq.z - qwq.x, 1.0 - (qx2 + qy2));
    return mat3(m0, m1, m2);
}

float XRENGINE_ComputeInfluenceWeight(int probeIndex, vec3 worldPos)
{
    ProbeParam p = ProbeParams[probeIndex];
    int shape = int(p.InfluenceOffsetShape.w + 0.5);
    vec3 center = ProbePositions[probeIndex].xyz + p.InfluenceOffsetShape.xyz;
    if (shape == 0)
    {
        float inner = p.InfluenceInner.x;
        float outer = max(p.InfluenceOuter.x, inner + 0.0001);
        float dist = length(worldPos - center);
        float ndf = clamp((dist - inner) / (outer - inner), 0.0, 1.0);
        return 1.0 - ndf;
    }

    vec3 inner = p.InfluenceInner.xyz;
    vec3 outer = max(p.InfluenceOuter.xyz, inner + vec3(0.0001));
    vec3 rel = abs(worldPos - center);
    vec3 ndf3 = clamp((rel - inner) / (outer - inner), 0.0, 1.0);
    return 1.0 - max(ndf3.x, max(ndf3.y, ndf3.z));
}

vec3 XRENGINE_ApplyParallax(int probeIndex, vec3 dirWS, vec3 worldPos)
{
    ProbeParam p = ProbeParams[probeIndex];
    if (p.ProxyCenterEnable.w < 0.5)
        return dirWS;

    vec3 proxyCenter = ProbePositions[probeIndex].xyz + p.ProxyCenterEnable.xyz;
    vec3 halfExt = max(p.ProxyHalfExtents.xyz, vec3(0.0001));
    mat3 rot = XRENGINE_QuaternionToMat3(p.ProxyRotation);
    mat3 invRot = transpose(rot);

    vec3 rayOrigLS = invRot * (worldPos - proxyCenter);
    vec3 rayDirLS = normalize(invRot * dirWS);

    vec3 safeDir = vec3(
        abs(rayDirLS.x) < 1e-6 ? (rayDirLS.x >= 0.0 ? 1e-6 : -1e-6) : rayDirLS.x,
        abs(rayDirLS.y) < 1e-6 ? (rayDirLS.y >= 0.0 ? 1e-6 : -1e-6) : rayDirLS.y,
        abs(rayDirLS.z) < 1e-6 ? (rayDirLS.z >= 0.0 ? 1e-6 : -1e-6) : rayDirLS.z);
    vec3 t1 = (halfExt - rayOrigLS) / safeDir;
    vec3 t2 = (-halfExt - rayOrigLS) / safeDir;
    vec3 tmax = max(t1, t2);
    float dist = min(tmax.x, min(tmax.y, tmax.z));
    if (dist <= 0.0 || isnan(dist) || isinf(dist))
        return dirWS;

    vec3 hitLS = rayOrigLS + rayDirLS * dist;
    vec3 hitWS = proxyCenter + rot * hitLS;
    return normalize(hitWS - ProbePositions[probeIndex].xyz);
}

bool XRENGINE_ComputeBarycentric(vec3 p, vec3 a, vec3 b, vec3 c, vec3 d, out vec4 bary)
{
    mat3 m = mat3(b - a, c - a, d - a);
    vec3 v = p - a;
    vec3 uvw = inverse(m) * v;
    float w = 1.0 - uvw.x - uvw.y - uvw.z;
    bary = vec4(uvw, w);
    return bary.x >= -0.0001 && bary.y >= -0.0001 && bary.z >= -0.0001 && bary.w >= -0.0001;
}

void XRENGINE_ResolveProbeWeights(vec3 worldPos, out vec4 weights, out ivec4 indices)
{
    weights = vec4(0.0);
    indices = ivec4(-1);

    for (int i = 0; i < TetraCount; ++i)
    {
        ivec4 idx = ivec4(TetraIndices[i]);
        if (idx.x < 0 || idx.w < 0 || idx.x >= ProbeCount || idx.y >= ProbeCount || idx.z >= ProbeCount || idx.w >= ProbeCount)
            continue;

        vec4 bary;
        if (XRENGINE_ComputeBarycentric(worldPos, ProbePositions[idx.x].xyz, ProbePositions[idx.y].xyz, ProbePositions[idx.z].xyz, ProbePositions[idx.w].xyz, bary))
        {
            weights = bary;
            indices = idx;
            return;
        }
    }

    float bestDistances[4] = float[](1e20, 1e20, 1e20, 1e20);
    int bestIndices[4] = int[](-1, -1, -1, -1);
    for (int i = 0; i < ProbeCount; ++i)
    {
        float dist = length(worldPos - ProbePositions[i].xyz);
        for (int k = 0; k < 4; ++k)
        {
            if (dist < bestDistances[k])
            {
                for (int s = 3; s > k; --s)
                {
                    bestDistances[s] = bestDistances[s - 1];
                    bestIndices[s] = bestIndices[s - 1];
                }
                bestDistances[k] = dist;
                bestIndices[k] = i;
                break;
            }
        }
    }

    float sum = 0.0;
    for (int k = 0; k < 4; ++k)
    {
        if (bestIndices[k] >= 0)
        {
            float weight = 1.0 / max(bestDistances[k], 0.0001);
            weights[k] = weight;
            indices[k] = bestIndices[k];
            sum += weight;
        }
    }

    if (sum > 0.0)
        weights /= sum;
}

void XRENGINE_ResolveProbeWeightsGrid(vec3 worldPos, out vec4 weights, out ivec4 indices)
{
    weights = vec4(0.0);
    indices = ivec4(-1);

    if (ProbeGridDims.x <= 0 || ProbeGridDims.y <= 0 || ProbeGridDims.z <= 0 || ProbeGridCellSize <= 0.0)
        return;

    vec3 rel = (worldPos - ProbeGridOrigin) / ProbeGridCellSize;
    ivec3 cell = clamp(ivec3(floor(rel)), ivec3(0), ProbeGridDims - ivec3(1));
    int flatIndex = cell.x + cell.y * ProbeGridDims.x + cell.z * ProbeGridDims.x * ProbeGridDims.y;
    ivec2 offsetCount = CellOffsetCount[flatIndex];
    if (offsetCount.y <= 0)
        return;

    float bestDistances[4] = float[](1e20, 1e20, 1e20, 1e20);
    int bestIndices[4] = int[](-1, -1, -1, -1);
    for (int i = 0; i < offsetCount.y; ++i)
    {
        int probeIndex = CellProbeIndices[offsetCount.x + i];
        if (probeIndex < 0 || probeIndex >= ProbeCount)
            continue;

        float dist = length(worldPos - ProbePositions[probeIndex].xyz);
        for (int k = 0; k < 4; ++k)
        {
            if (dist < bestDistances[k])
            {
                for (int s = 3; s > k; --s)
                {
                    bestDistances[s] = bestDistances[s - 1];
                    bestIndices[s] = bestIndices[s - 1];
                }
                bestDistances[k] = dist;
                bestIndices[k] = probeIndex;
                break;
            }
        }
    }

    float sum = 0.0;
    for (int k = 0; k < 4; ++k)
    {
        if (bestIndices[k] >= 0)
        {
            float weight = 1.0 / max(bestDistances[k], 0.0001);
            weights[k] = weight;
            indices[k] = bestIndices[k];
            sum += weight;
        }
    }

    if (sum > 0.0)
        weights /= sum;
}

vec3 XRENGINE_CalculateAmbientPbr(vec3 normal, vec3 fragPos, vec3 albedo, vec3 viewDir, vec3 rms, float ambientOcclusion)
{
    float roughness = rms.x;
    float metallic = rms.y;
    float specularIntensity = rms.z;

    float NoV = max(dot(normal, viewDir), 0.0);
    vec3 F0 = mix(vec3(0.04), albedo, metallic);
    vec3 kS = XRENGINE_SpecF_SchlickRoughnessApprox(NoV, F0, roughness) * specularIntensity;
    vec3 kD = (1.0 - kS) * (1.0 - metallic);
    vec3 diffuseAO = AmbientOcclusionMultiBounce
        ? XRENGINE_MultiBounceAO(ambientOcclusion, albedo)
        : vec3(ambientOcclusion);
    float specularOcclusion = SpecularOcclusionEnabled
        ? XRENGINE_GTSpecularOcclusion(NoV, ambientOcclusion, roughness)
        : ambientOcclusion;

    if (!ForwardPbrResourcesEnabled || ProbeCount <= 0)
        return GlobalAmbient * albedo * diffuseAO;

    vec4 probeWeights;
    ivec4 probeIndices;
    if (UseProbeGrid)
    {
        XRENGINE_ResolveProbeWeightsGrid(fragPos, probeWeights, probeIndices);
        if (probeIndices.x < 0 && probeIndices.y < 0 && probeIndices.z < 0 && probeIndices.w < 0)
            XRENGINE_ResolveProbeWeights(fragPos, probeWeights, probeIndices);
    }
    else
    {
        XRENGINE_ResolveProbeWeights(fragPos, probeWeights, probeIndices);
    }

    vec3 reflectionDir = reflect(-viewDir, normal);
    vec3 irradianceColor = vec3(0.0);
    vec3 prefilteredColor = vec3(0.0);
    float totalWeight = 0.0;

    for (int i = 0; i < 4; ++i)
    {
        if (probeIndices[i] < 0)
            continue;

        float influence = XRENGINE_ComputeInfluenceWeight(probeIndices[i], fragPos);
        float weight = probeWeights[i] * influence;
        if (weight <= 0.0)
            continue;

        vec3 diffuseDir = XRENGINE_ApplyParallax(probeIndices[i], normal, fragPos);
        vec3 specDir = XRENGINE_ApplyParallax(probeIndices[i], reflectionDir, fragPos);
        float normalizationScale = max(ProbeParams[probeIndices[i]].ProxyHalfExtents.w, 0.0001);
        float maxMip = float(textureQueryLevels(PrefilterArray) - 1);
        float clampedLod = min(roughness * MAX_REFLECTION_LOD, maxMip);

        irradianceColor += weight * normalizationScale * XRENGINE_SampleOctaArray(IrradianceArray, diffuseDir, probeIndices[i]);
        prefilteredColor += weight * normalizationScale * XRENGINE_SampleOctaArrayLod(PrefilterArray, specDir, probeIndices[i], clampedLod);
        totalWeight += weight;
    }

    if (totalWeight > 0.0)
    {
        irradianceColor /= totalWeight;
        prefilteredColor /= totalWeight;
    }
    else
    {
        float maxMip = float(textureQueryLevels(PrefilterArray) - 1);
        float clampedLod = min(roughness * MAX_REFLECTION_LOD, maxMip);
        irradianceColor = XRENGINE_SampleOctaArray(IrradianceArray, normal, 0.0);
        prefilteredColor = XRENGINE_SampleOctaArrayLod(PrefilterArray, normal, 0.0, clampedLod);
    }

    vec2 brdfValue = texture(BRDF, vec2(NoV, roughness)).rg;
    vec3 diffuse = irradianceColor * albedo;
    vec3 specular = prefilteredColor * (kS * brdfValue.x + brdfValue.y);
    return kD * diffuse * diffuseAO + specular * specularOcclusion;
}

float XRENGINE_GetShadowBias(float diffuseFactor)
{
    float mapped = pow(ShadowBase * (1.0 - max(diffuseFactor, 0.0)), ShadowMult);
    return mix(ShadowBiasMin, ShadowBiasMax, mapped);
}

// Shadow map reading for primary directional light (uses standalone ShadowMap sampler)
float XRENGINE_ReadShadowMapDir(vec3 fragPos, vec3 normal, float diffuseFactor)
{
    if (!ShadowMapEnabled)
        return 1.0;

    mat4 lightMatrix = PrimaryDirLightWorldToLightProjMatrix * inverse(PrimaryDirLightWorldToLightInvViewMatrix);
    vec4 fragPosLightSpace = lightMatrix * vec4(fragPos, 1.0);
    vec3 fragCoord = fragPosLightSpace.xyz / fragPosLightSpace.w;
    fragCoord = fragCoord * 0.5 + 0.5;

    // Outside shadow map bounds: treat as fully lit
    if (fragCoord.x < 0.0 || fragCoord.x > 1.0 ||
        fragCoord.y < 0.0 || fragCoord.y > 1.0 ||
        fragCoord.z < 0.0 || fragCoord.z > 1.0)
        return 1.0;

    float bias = XRENGINE_GetShadowBias(diffuseFactor);

    float shadow = 0.0;
    vec2 texelSize = 1.0 / vec2(textureSize(ShadowMap, 0));
    for (int x = -1; x <= 1; ++x)
    {
        for (int y = -1; y <= 1; ++y)
        {
            float pcfDepth = texture(ShadowMap, fragCoord.xy + vec2(x, y) * texelSize).r;
            shadow += (fragCoord.z - bias) > pcfDepth ? 0.0 : 1.0;
        }
    }

    return shadow * 0.111111111;
}

vec3 XRENGINE_CalculateDirectPbrLight(vec3 lightColor, float diffuseIntensity, vec3 lightDirection, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0, float attenuation)
{
    vec3 viewDir = normalize(CameraPosition - fragPos);
    vec3 halfVector = normalize(viewDir + lightDirection);
    float NoL = max(dot(normal, lightDirection), 0.0);
    if (NoL <= 0.0)
        return vec3(0.0);

    float NoH = max(dot(normal, halfVector), 0.0);
    float NoV = max(dot(normal, viewDir), 0.0);
    float HoV = max(dot(halfVector, viewDir), 0.0);
    float roughness = rms.x;
    float metallic = rms.y;
    float specularIntensity = rms.z;
    float a = roughness * roughness;
    float k = (roughness + 1.0);
    k = k * k * 0.125;

    float D = XRENGINE_SpecD_TRGGX(NoH * NoH, a * a);
    float G = XRENGINE_SpecG_Smith(NoV, NoL, k);
    vec3 F = XRENGINE_SpecF_SchlickApprox(HoV, F0);
    vec3 specular = specularIntensity * D * G * F / (4.0 * NoV * NoL + 0.0001);
    vec3 kD = (1.0 - F) * (1.0 - metallic);
    vec3 radiance = attenuation * lightColor * diffuseIntensity;
    return (kD * albedo / PI + specular) * radiance * NoL;
}

vec3 XRENGINE_CalcDirLight(DirLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0, bool useShadow)
{
    vec3 lightDir = normalize(-light.Direction);
    float shadow = useShadow ? XRENGINE_ReadShadowMapDir(fragPos, normal, max(dot(normal, lightDir), 0.0)) : 1.0;
    return XRENGINE_CalculateDirectPbrLight(light.Base.Color, light.Base.DiffuseIntensity, lightDir, normal, fragPos, albedo, rms, F0, 1.0) * shadow;
}

vec3 XRENGINE_CalcPointLight(PointLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0)
{
    vec3 lightVector = light.Position - fragPos;
    float attenuation = XRENGINE_Attenuate(length(lightVector) / max(light.Brightness, 0.0001), light.Radius / max(light.Brightness, 0.0001));
    return XRENGINE_CalculateDirectPbrLight(light.Base.Color, light.Base.DiffuseIntensity, normalize(lightVector), normal, fragPos, albedo, rms, F0, attenuation);
}

vec3 XRENGINE_CalcSpotLight(SpotLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0)
{
    vec3 lightVector = light.Base.Position - fragPos;
    vec3 lightDir = normalize(lightVector);
    float clampedCosine = max(0.0, dot(-lightDir, normalize(light.Direction)));
    float spotEffect = smoothstep(light.OuterCutoff, light.InnerCutoff, clampedCosine);
    float spotAttn = pow(clampedCosine, light.Exponent);
    float distAttn = XRENGINE_Attenuate(length(lightVector) / max(light.Base.Brightness, 0.0001), light.Base.Radius / max(light.Base.Brightness, 0.0001));
    return spotEffect * spotAttn * XRENGINE_CalculateDirectPbrLight(light.Base.Base.Color, light.Base.Base.DiffuseIntensity, lightDir, normal, fragPos, albedo, rms, F0, distAttn);
}

vec3 XRENGINE_CalcForwardPlusColor(vec3 lightColor, float diffuseIntensity, vec3 lightDirection, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0, float attenuation)
{
    return XRENGINE_CalculateDirectPbrLight(lightColor, diffuseIntensity, lightDirection, normal, fragPos, albedo, rms, F0, attenuation);
}

vec3 XRENGINE_CalcForwardPlusPointLight(ForwardPlusLocalLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0)
{
    vec3 lightVector = light.PositionWS.xyz - fragPos;
    float attenuation = XRENGINE_Attenuate(length(lightVector) / max(light.Params.y, 0.0001), light.Params.x / max(light.Params.y, 0.0001));
    return XRENGINE_CalcForwardPlusColor(light.Color_Type.xyz, light.Params.z, normalize(lightVector), normal, fragPos, albedo, rms, F0, attenuation);
}

vec3 XRENGINE_CalcForwardPlusSpotLight(ForwardPlusLocalLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0)
{
    vec3 lightDir = normalize(light.DirectionWS_Exponent.xyz);
    vec3 lightVector = light.PositionWS.xyz - fragPos;
    vec3 lightToPosN = normalize(lightVector);
    float clampedCosine = max(0.0, dot(-lightToPosN, lightDir));
    float spotEffect = smoothstep(light.SpotAngles.y, light.SpotAngles.x, clampedCosine);

    float spotAttn = pow(clampedCosine, light.DirectionWS_Exponent.w);
    float distAttn = XRENGINE_Attenuate(length(lightVector) / max(light.Params.y, 0.0001), light.Params.x);

    return spotEffect * spotAttn * XRENGINE_CalcForwardPlusColor(light.Color_Type.xyz, light.Params.z, lightToPosN, normal, fragPos, albedo, rms, F0, distAttn);
}

// Main lighting calculation function
// Call this from your fragment shader main() with your surface parameters
vec3 XRENGINE_CalculateForwardLighting(vec3 normal, vec3 fragPos, vec3 albedo, float specularIntensity, float ambientOcclusion)
{
    normal = normalize(normal);
    vec3 viewDir = normalize(CameraPosition - fragPos);
    vec3 rms = vec3(clamp(Roughness, 0.0, 1.0), clamp(Metallic, 0.0, 1.0), max(specularIntensity, 0.0));
    vec3 F0 = mix(vec3(0.04), albedo, rms.y);
    vec3 totalLight = vec3(0.0);

    // Directional lights (first one uses shadow map if available)
    for (int i = 0; i < DirLightCount; ++i)
    {
        // Only the first directional light uses the bound shadow map
        bool useShadow = (i == 0);
        totalLight += XRENGINE_CalcDirLight(DirectionalLights[i], normal, fragPos, albedo, rms, F0, useShadow);
    }

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
                ? XRENGINE_CalcForwardPlusPointLight(l, normal, fragPos, albedo, rms, F0)
                : XRENGINE_CalcForwardPlusSpotLight(l, normal, fragPos, albedo, rms, F0);
        }
    }
    else
    {
        for (int i = 0; i < PointLightCount; ++i)
            totalLight += XRENGINE_CalcPointLight(PointLights[i], normal, fragPos, albedo, rms, F0);

        for (int i = 0; i < SpotLightCount; ++i)
            totalLight += XRENGINE_CalcSpotLight(SpotLights[i], normal, fragPos, albedo, rms, F0);
    }

    return totalLight + XRENGINE_CalculateAmbientPbr(normal, fragPos, albedo, viewDir, rms, ambientOcclusion) + albedo * Emission;
}
