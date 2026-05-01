// Forward Lighting Snippet - PBR Forward+ lighting with probe-based ambient
// Requires: CameraPosition (vec3), normal (vec3), fragPos (vec3)

#pragma snippet "LightStructs"
#pragma snippet "LightAttenuation"
#pragma snippet "ShadowSampling"

const float PI = 3.14159265359;
const float MAX_REFLECTION_LOD = 4.0;
const float XRENGINE_MIN_FORWARD_AMBIENT_FALLBACK = 0.08;

uniform vec3 GlobalAmbient;
uniform float Roughness = 0.9;
uniform float Metallic = 0.0;
uniform float Emission = 0.0;
uniform bool AmbientOcclusionMultiBounce;
uniform bool SpecularOcclusionEnabled;
uniform bool ForwardPbrResourcesEnabled;
uniform mat4 ViewMatrix;
uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform mat4 ProjMatrix;
uniform mat4 ViewProjectionMatrix;
uniform mat4 LeftEyeInverseViewMatrix;
uniform mat4 RightEyeInverseViewMatrix;
uniform mat4 LeftEyeInverseProjMatrix;
uniform mat4 RightEyeInverseProjMatrix;
uniform mat4 LeftEyeProjMatrix;
uniform mat4 RightEyeProjMatrix;
uniform mat4 LeftEyeViewProjectionMatrix;
uniform mat4 RightEyeViewProjectionMatrix;
layout(location = 22) in float FragViewIndex;

#ifndef XRENGINE_DEPTH_MODE_UNIFORM
#define XRENGINE_DEPTH_MODE_UNIFORM
uniform int DepthMode;
#endif

#ifndef XRENGINE_SCREEN_SIZE_UNIFORMS
#define XRENGINE_SCREEN_SIZE_UNIFORMS
uniform float ScreenWidth;
uniform float ScreenHeight;
#endif

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

struct ProbeGridCell
{
    ivec4 OffsetCount;
    ivec4 FallbackIndices;
};

layout(std430, binding = 3) readonly buffer LightProbeGridCells
{
    ProbeGridCell GridCells[];
};

layout(std430, binding = 4) readonly buffer LightProbeGridIndices
{
    int CellTetraIndices[];
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
layout(binding = 16) uniform sampler2DArray ShadowMapArray;
uniform bool ShadowMapEnabled;
uniform bool UseCascadedDirectionalShadows;
uniform mat4 PrimaryDirLightWorldToLightInvViewMatrix;
uniform mat4 PrimaryDirLightWorldToLightProjMatrix;
uniform ivec4 ShadowPackedI0 = ivec4(8, 8, 5, 2); // blocker samples, filter samples, vogel taps, soft mode
uniform ivec4 ShadowPackedI1 = ivec4(1, 1, 16, 0); // cascades enabled, contact enabled, contact samples, reserved
uniform vec4 ShadowParams0 = vec4(0.035, 1.221, 0.00001, 0.004); // base, exponent, bias min, bias max
uniform vec4 ShadowParams1 = vec4(0.0012, 0.01, 0.001, 0.015); // filter radius, blocker radius, min penumbra, max penumbra
uniform vec4 ShadowParams2 = vec4(1.2, 1.0, 2.0, 10.0); // source radius, contact distance, contact thickness, contact fade start
uniform vec4 ShadowParams3 = vec4(40.0, 0.0, 1.0, 0.0); // contact fade end, normal offset, jitter, reserved
uniform bool DirectionalShadowAtlasEnabled = false;
uniform ivec4 DirectionalShadowAtlasPacked0[XRENGINE_MAX_CASCADES]; // enabled, page, fallback, record index
uniform vec4 DirectionalShadowAtlasParams0[XRENGINE_MAX_CASCADES];  // uv scale/bias
uniform vec4 DirectionalShadowAtlasParams1[XRENGINE_MAX_CASCADES];  // near, far, local texel size, reserved

#define ShadowBlockerSamples ShadowPackedI0.x
#define ShadowFilterSamples ShadowPackedI0.y
#define ShadowVogelTapCount ShadowPackedI0.z
#define SoftShadowMode ShadowPackedI0.w
#define EnableCascadedShadows (ShadowPackedI1.x != 0)
#define EnableContactShadows (ShadowPackedI1.y != 0)
#define ContactShadowSamples ShadowPackedI1.z
#define ShadowBase ShadowParams0.x
#define ShadowMult ShadowParams0.y
#define ShadowBiasMin ShadowParams0.z
#define ShadowBiasMax ShadowParams0.w
#define ShadowFilterRadius ShadowParams1.x
#define ShadowBlockerSearchRadius ShadowParams1.y
#define ShadowMinPenumbra ShadowParams1.z
#define ShadowMaxPenumbra ShadowParams1.w
#define LightSourceRadius ShadowParams2.x
#define ContactShadowDistance ShadowParams2.y
#define ContactShadowThickness ShadowParams2.z
#define ContactShadowFadeStart ShadowParams2.w
#define ContactShadowFadeEnd ShadowParams3.x
#define ContactShadowNormalOffset ShadowParams3.y
#define ContactShadowJitterStrength ShadowParams3.z

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

const int XRENGINE_MAX_FORWARD_POINT_SHADOW_SLOTS = 4;
const int XRENGINE_MAX_FORWARD_SPOT_SHADOW_SLOTS = 4;
const int XRENGINE_MAX_FORWARD_SPOT_ATLAS_PAGES = 2;

layout(binding = 17) uniform samplerCube PointLightShadowMaps[XRENGINE_MAX_FORWARD_POINT_SHADOW_SLOTS];
layout(binding = 21) uniform sampler2D SpotLightShadowMaps[XRENGINE_MAX_FORWARD_SPOT_SHADOW_SLOTS];
layout(binding = 26) uniform sampler2D ForwardContactDepthView;
layout(binding = 27) uniform sampler2D ForwardContactNormalView;
layout(binding = 28) uniform sampler2DArray ForwardContactDepthViewArray;
layout(binding = 29) uniform sampler2DArray ForwardContactNormalViewArray;
layout(binding = 9) uniform sampler2D DirectionalShadowAtlasPages[XRENGINE_MAX_FORWARD_SPOT_ATLAS_PAGES];
layout(binding = 30) uniform sampler2D SpotLightShadowAtlasPages[XRENGINE_MAX_FORWARD_SPOT_ATLAS_PAGES];
uniform bool ForwardContactShadowsEnabled = false;
uniform bool ForwardContactShadowsArrayEnabled = false;

struct ForwardPointShadowData
{
    ivec4 Packed0; // slot, filter samples, blocker samples, vogel taps
    ivec4 Packed1; // soft mode, debug mode, contact enabled, contact samples
    vec4 Params0;  // near plane, far plane, shadow base, shadow exponent
    vec4 Params1;  // bias min, bias max, filter radius, blocker radius
    vec4 Params2;  // min penumbra, max penumbra, source radius, contact distance
    vec4 Params3;  // contact thickness, fade start, fade end, normal offset
    vec4 Params4;  // contact jitter, reserved
};

struct ForwardSpotShadowData
{
    ivec4 Packed0; // slot, filter samples, blocker samples, vogel taps
    ivec4 Packed1; // soft mode, debug mode, contact enabled, contact samples
    vec4 Params0;  // shadow base, shadow exponent, bias min, bias max
    vec4 Params1;  // filter radius, blocker radius, min penumbra, max penumbra
    vec4 Params2;  // source radius, contact distance, contact thickness, fade start
    vec4 Params3;  // fade end, normal offset, jitter, reserved
    ivec4 Packed2; // encoding, use moment mipmaps, reserved, reserved
    vec4 Params4;  // moment min variance, bleed reduction, positive exponent, negative exponent
    vec4 Params5;  // shadow near, shadow far, moment mip bias, reserved
    ivec4 AtlasPacked0; // enabled, page, fallback, record index
    vec4 AtlasParams0;  // uv scale/bias
    vec4 AtlasParams1;  // near, far, local texel size, reserved
};

layout(std430, binding = 27) readonly buffer ForwardPointShadowMetadataBuffer
{
    ForwardPointShadowData PointLightShadows[];
};

layout(std430, binding = 28) readonly buffer ForwardSpotShadowMetadataBuffer
{
    ForwardSpotShadowData SpotLightShadows[];
};

// Forward+ tiled light culling uniforms
uniform bool ForwardPlusEnabled;
uniform vec2 ForwardPlusScreenSize;
uniform int ForwardPlusTileSize;
// Precomputed tile counts (ceil(screen / tileSize)) — supplied by the engine
// so shaders don't have to recompute them per fragment.
uniform int ForwardPlusTileCountX;
uniform int ForwardPlusTileCountY;
uniform int ForwardPlusMaxLightsPerTile;
uniform int ForwardPlusEyeCount;
#ifndef XRENGINE_SCREEN_ORIGIN_UNIFORM
#define XRENGINE_SCREEN_ORIGIN_UNIFORM
uniform vec2 ScreenOrigin;
#endif

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

int XRENGINE_ForwardShadowDebugModeActive = 0;
vec3 XRENGINE_ForwardShadowDebugColor = vec3(0.0);

vec3 XRENGINE_ResolveForwardShadowDebugColor(int debugMode, float lit, float margin)
{
    if (debugMode == 1)
        return vec3(lit);

    if (debugMode == 2)
    {
        float intensity = min(abs(margin) * 20.0, 1.0);
        float clampedLit = clamp(lit, 0.0, 1.0);
        return vec3((1.0 - clampedLit) * intensity, clampedLit * intensity, 0.0);
    }

    return vec3(0.0);
}

void XRENGINE_TrySetForwardShadowDebug(int debugMode, float lit, float margin)
{
    if (debugMode == 0 || XRENGINE_ForwardShadowDebugModeActive != 0)
        return;

    XRENGINE_ForwardShadowDebugModeActive = debugMode;
    XRENGINE_ForwardShadowDebugColor = XRENGINE_ResolveForwardShadowDebugColor(debugMode, lit, margin);
}

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

int XRENGINE_GetForwardViewIndex()
{
    if (ForwardPlusEyeCount <= 1)
        return 0;

    return int(clamp(round(FragViewIndex), 0.0, float(ForwardPlusEyeCount - 1)));
}

mat4 XRENGINE_GetForwardInverseViewMatrix()
{
    if (ForwardPlusEyeCount <= 1)
        return InverseViewMatrix;

    return XRENGINE_GetForwardViewIndex() == 0
        ? LeftEyeInverseViewMatrix
        : RightEyeInverseViewMatrix;
}

mat4 XRENGINE_GetForwardInverseProjMatrix()
{
    if (ForwardPlusEyeCount <= 1)
        return InverseProjMatrix;

    return XRENGINE_GetForwardViewIndex() == 0
        ? LeftEyeInverseProjMatrix
        : RightEyeInverseProjMatrix;
}

mat4 XRENGINE_GetForwardProjMatrix()
{
    if (ForwardPlusEyeCount <= 1)
        return ProjMatrix;

    return XRENGINE_GetForwardViewIndex() == 0
        ? LeftEyeProjMatrix
        : RightEyeProjMatrix;
}

mat4 XRENGINE_GetForwardViewProjectionMatrix()
{
    if (ForwardPlusEyeCount <= 1)
        return ViewProjectionMatrix;

    return XRENGINE_GetForwardViewIndex() == 0
        ? LeftEyeViewProjectionMatrix
        : RightEyeViewProjectionMatrix;
}

vec3 XRENGINE_GetForwardCameraPosition()
{
    mat4 inverseView = XRENGINE_GetForwardInverseViewMatrix();
    return inverseView[3].xyz;
}

float XRENGINE_GetLocalShadowBias(float NoL, float shadowBase, float shadowExponent, float minBias, float maxBias)
{
    float mapped = pow(max(shadowBase, 0.0) * (1.0 - NoL), max(shadowExponent, 0.0001));
    return mix(minBias, maxBias, clamp(mapped, 0.0, 1.0));
}

float XRENGINE_GetPointShadowSampleRadius(samplerCube shadowMap, float filterRadius)
{
    float faceSize = max(float(textureSize(shadowMap, 0).x), 1.0);
    float texelDirectionSpan = 2.0 / faceSize;
    float requestedScale = clamp(filterRadius * 256.0, 0.0, 4.0);
    return texelDirectionSpan * max(1.0, requestedScale);
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
        float inner = p.InfluenceInner.w;
        float outer = max(p.InfluenceOuter.w, inner + 0.0001);
        float dist = length(worldPos - center);
        return smoothstep(outer, inner, dist);
    }

    vec3 inner = p.InfluenceInner.xyz;
    vec3 outer = max(p.InfluenceOuter.xyz, inner + vec3(0.0001));
    vec3 rel = abs(worldPos - center);
    vec3 ndf3 = clamp((rel - inner) / (outer - inner), 0.0, 1.0);
    float ndf = max(ndf3.x, max(ndf3.y, ndf3.z));
    return smoothstep(1.0, 0.0, ndf);
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
    bary = vec4(w, uvw);
    return bary.x >= -0.0001 && bary.y >= -0.0001 && bary.z >= -0.0001 && bary.w >= -0.0001;
}

#ifdef XRENGINE_PROBE_DEBUG_FALLBACK
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
#endif // XRENGINE_PROBE_DEBUG_FALLBACK

void XRENGINE_ResolveProbeWeightsGrid(vec3 worldPos, out vec4 weights, out ivec4 indices, out bool isBarycentric)
{
    weights = vec4(0.0);
    indices = ivec4(-1);
    isBarycentric = false;

    if (ProbeGridDims.x <= 0 || ProbeGridDims.y <= 0 || ProbeGridDims.z <= 0 || ProbeGridCellSize <= 0.0)
        return;

    vec3 rel = (worldPos - ProbeGridOrigin) / ProbeGridCellSize;
    ivec3 cell = clamp(ivec3(floor(rel)), ivec3(0), ProbeGridDims - ivec3(1));
    int flatIndex = cell.x + cell.y * ProbeGridDims.x + cell.z * ProbeGridDims.x * ProbeGridDims.y;
    ProbeGridCell cellData = GridCells[flatIndex];
    ivec2 offsetCount = cellData.OffsetCount.xy;

    if (offsetCount.y > 0)
    {
        for (int i = 0; i < offsetCount.y; ++i)
        {
            int tetraIndex = CellTetraIndices[offsetCount.x + i];
            if (tetraIndex < 0 || tetraIndex >= TetraCount)
                continue;

            ivec4 idx = ivec4(TetraIndices[tetraIndex]);
            if (idx.x < 0 || idx.w < 0 || idx.x >= ProbeCount || idx.y >= ProbeCount || idx.z >= ProbeCount || idx.w >= ProbeCount)
                continue;

            vec4 bary;
            if (XRENGINE_ComputeBarycentric(worldPos, ProbePositions[idx.x].xyz, ProbePositions[idx.y].xyz, ProbePositions[idx.z].xyz, ProbePositions[idx.w].xyz, bary))
            {
                weights = bary;
                indices = idx;
                isBarycentric = true;
                return;
            }
        }
    }

    float sum = 0.0;
    for (int k = 0; k < 4; ++k)
    {
        int probeIndex = cellData.FallbackIndices[k];
        if (probeIndex < 0 || probeIndex >= ProbeCount)
            continue;

        float dist = length(worldPos - ProbePositions[probeIndex].xyz);
        float weight = 1.0 / max(dist, 0.0001);
        weights[k] = weight;
        indices[k] = probeIndex;
        sum += weight;
    }

    if (sum > 0.0)
        weights /= sum;
}

vec3 XRENGINE_ResolveForwardAmbientFallback()
{
    float ambientMax = max(GlobalAmbient.r, max(GlobalAmbient.g, GlobalAmbient.b));
    if (ambientMax <= 0.0001)
        return vec3(XRENGINE_MIN_FORWARD_AMBIENT_FALLBACK);

    float scale = max(1.0, XRENGINE_MIN_FORWARD_AMBIENT_FALLBACK / ambientMax);
    return GlobalAmbient * scale;
}

vec3 XRENGINE_GetForwardAmbientFallback(vec3 albedo, vec3 diffuseAO)
{
    return XRENGINE_ResolveForwardAmbientFallback() * albedo * diffuseAO;
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
        return XRENGINE_GetForwardAmbientFallback(albedo, diffuseAO);

    vec4 probeWeights = vec4(0.0);
    ivec4 probeIndices = ivec4(-1);
    bool isBarycentric = false;
    if (UseProbeGrid)
        XRENGINE_ResolveProbeWeightsGrid(fragPos, probeWeights, probeIndices, isBarycentric);
#ifdef XRENGINE_PROBE_DEBUG_FALLBACK
    else
        XRENGINE_ResolveProbeWeights(fragPos, probeWeights, probeIndices);
#endif

    vec3 reflectionDir = reflect(-viewDir, normal);
    vec3 irradianceColor = vec3(0.0);
    vec3 prefilteredColor = vec3(0.0);
    float totalWeight = 0.0;

    for (int i = 0; i < 4; ++i)
    {
        if (probeIndices[i] < 0)
            continue;

        // Barycentric tetra weights are already mathematically continuous.
        // Multiplying by per-probe influence would create hard edges at
        // influence-sphere boundaries, so we only apply influence for the
        // distance-weighted fallback path.
        float weight = isBarycentric
            ? probeWeights[i]
            : probeWeights[i] * XRENGINE_ComputeInfluenceWeight(probeIndices[i], fragPos);
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

    if (totalWeight <= 0.0)
    {
        return XRENGINE_GetForwardAmbientFallback(albedo, diffuseAO);
    }

    irradianceColor /= totalWeight;
    prefilteredColor /= totalWeight;

    vec2 brdfValue = texture(BRDF, vec2(NoV, roughness)).rg;
    vec3 diffuse = GlobalAmbient * irradianceColor * albedo;
    vec3 specular = prefilteredColor * (kS * brdfValue.x + brdfValue.y);
    return kD * diffuse * diffuseAO + specular * specularOcclusion;
}

float XRENGINE_GetShadowBiasRange(float diffuseFactor, float biasMin, float biasMax)
{
    float mapped = pow(max(ShadowBase, 0.0) * (1.0 - max(diffuseFactor, 0.0)), max(ShadowMult, 0.0001));
    return mix(biasMin, biasMax, clamp(mapped, 0.0, 1.0));
}

float XRENGINE_GetShadowBias(float diffuseFactor)
{
    return XRENGINE_GetShadowBiasRange(diffuseFactor, ShadowBiasMin, ShadowBiasMax);
}

float XRENGINE_ViewDepthFromWorldPos(vec3 fragPosWS)
{
    mat4 inverseView = XRENGINE_GetForwardInverseViewMatrix();
    vec3 cameraForwardWS = normalize(inverseView[2].xyz);
    return abs(dot(fragPosWS - inverseView[3].xyz, cameraForwardWS));
}

float XRENGINE_SampleForwardContactShadowScreenSpace(
    vec3 fragPosWS,
    vec3 normalWS,
    vec3 lightDirWS,
    float receiverOffset,
    float compareBias,
    float contactDistance,
    int contactSamples,
    float contactThickness,
    float contactFadeStart,
    float contactFadeEnd,
    float contactNormalOffset,
    float jitterStrength,
    float viewDepth)
{
    if (ForwardContactShadowsArrayEnabled)
    {
        return XRENGINE_SampleContactShadowScreenSpace(
            ForwardContactDepthViewArray,
            ForwardContactNormalViewArray,
            float(XRENGINE_GetForwardViewIndex()),
            vec2(ScreenWidth, ScreenHeight),
            ViewMatrix,
            XRENGINE_GetForwardInverseProjMatrix(),
            XRENGINE_GetForwardInverseViewMatrix(),
            XRENGINE_GetForwardProjMatrix(),
            DepthMode,
            fragPosWS,
            normalWS,
            lightDirWS,
            receiverOffset,
            compareBias,
            contactDistance,
            contactSamples,
            contactThickness,
            contactFadeStart,
            contactFadeEnd,
            contactNormalOffset,
            jitterStrength,
            viewDepth);
    }

    return XRENGINE_SampleContactShadowScreenSpace(
        ForwardContactDepthView,
        ForwardContactNormalView,
        vec2(ScreenWidth, ScreenHeight),
        ViewMatrix,
        XRENGINE_GetForwardInverseProjMatrix(),
        XRENGINE_GetForwardInverseViewMatrix(),
        XRENGINE_GetForwardProjMatrix(),
        DepthMode,
        fragPosWS,
        normalWS,
        lightDirWS,
        receiverOffset,
        compareBias,
        contactDistance,
        contactSamples,
        contactThickness,
        contactFadeStart,
        contactFadeEnd,
        contactNormalOffset,
        jitterStrength,
        viewDepth);
}

float XRENGINE_SampleDirectionalAtlasPage(
    int pageIndex,
    vec3 atlasCoord,
    float bias,
    int blockerSamples,
    int filterSamples,
    float filterRadius,
    float blockerSearchRadius,
    int softMode,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra,
    int vogelTapCount)
{
    if (pageIndex == 0)
    {
        return XRENGINE_SampleShadowMapFiltered(
            DirectionalShadowAtlasPages[0],
            atlasCoord,
            bias,
            blockerSamples,
            filterSamples,
            filterRadius,
            blockerSearchRadius,
            softMode,
            lightSourceRadius,
            minPenumbra,
            maxPenumbra,
            vogelTapCount);
    }

    if (pageIndex == 1)
    {
        return XRENGINE_SampleShadowMapFiltered(
            DirectionalShadowAtlasPages[1],
            atlasCoord,
            bias,
            blockerSamples,
            filterSamples,
            filterRadius,
            blockerSearchRadius,
            softMode,
            lightSourceRadius,
            minPenumbra,
            maxPenumbra,
            vogelTapCount);
    }

    return 1.0;
}

// Returns the cascade shadow value for a specific cascade index.
// clampToEdge: when true, UV coords are clamped to [0,1] so the last cascade's
// shadow persists beyond its far split instead of dropping to fully-lit.
float XRENGINE_ReadCascadeShadowMapDir(vec3 fragPos, vec3 normal, float diffuseFactor, int cascadeIndex, bool clampToEdge)
{
    mat4 lightMatrix = DirectionalLights[0].CascadeMatrices[cascadeIndex];
    float receiverOffset = DirectionalLights[0].CascadeReceiverOffsets[cascadeIndex];
    vec3 offsetPosWS = fragPos + normal * receiverOffset;
    vec3 fragCoord = XRENGINE_ProjectShadowCoord(lightMatrix, offsetPosWS);

    if (clampToEdge)
        fragCoord.xy = clamp(fragCoord.xy, 0.0, 1.0);
    else if (!XRENGINE_ShadowCoordInBounds(fragCoord))
        return -1.0;

    float bias = XRENGINE_GetShadowBiasRange(
        diffuseFactor,
        DirectionalLights[0].CascadeBiasMin[cascadeIndex],
        DirectionalLights[0].CascadeBiasMax[cascadeIndex]);
    float viewDepth = XRENGINE_ViewDepthFromWorldPos(fragPos);
    int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
        ContactShadowSamples,
        viewDepth,
        ContactShadowDistance);
    vec3 lightDirWS = normalize(-DirectionalLights[0].Direction);
    float contact = 1.0;
    if (EnableContactShadows)
    {
        contact = ForwardContactShadowsEnabled
            ? XRENGINE_SampleForwardContactShadowScreenSpace(
                fragPos,
                normal,
                lightDirWS,
                receiverOffset,
                bias,
                ContactShadowDistance,
                contactSampleCount,
                ContactShadowThickness,
                ContactShadowFadeStart,
                ContactShadowFadeEnd,
                ContactShadowNormalOffset,
                ContactShadowJitterStrength,
                viewDepth)
            : XRENGINE_SampleContactShadowArray(
                ShadowMapArray,
                lightMatrix,
                float(cascadeIndex),
                fragPos,
                normal,
                lightDirWS,
                receiverOffset,
                bias,
                ContactShadowDistance,
                contactSampleCount,
                ContactShadowThickness,
                ContactShadowFadeStart,
                ContactShadowFadeEnd,
                ContactShadowNormalOffset,
                ContactShadowJitterStrength,
                viewDepth);
    }

    float cascadeScale = 1.0 + float(cascadeIndex) * 0.35;
    float filterRadius = ShadowFilterRadius * cascadeScale;

    if (DirectionalShadowAtlasEnabled)
    {
        ivec4 atlasI0 = DirectionalShadowAtlasPacked0[cascadeIndex];
        bool atlasEnabled = atlasI0.x != 0 && atlasI0.y >= 0 && atlasI0.y < XRENGINE_MAX_FORWARD_SPOT_ATLAS_PAGES;
        int fallbackMode = atlasI0.z;
        if (atlasEnabled)
        {
            vec4 atlasUvScaleBias = DirectionalShadowAtlasParams0[cascadeIndex];
            vec2 atlasUv = fragCoord.xy * atlasUvScaleBias.xy + atlasUvScaleBias.zw;
            float atlasRadiusScale = max(atlasUvScaleBias.x, atlasUvScaleBias.y);
            return XRENGINE_SampleDirectionalAtlasPage(
                atlasI0.y,
                vec3(atlasUv, fragCoord.z),
                bias,
                ShadowBlockerSamples,
                ShadowFilterSamples,
                filterRadius * atlasRadiusScale,
                ShadowBlockerSearchRadius * cascadeScale * atlasRadiusScale,
                SoftShadowMode,
                LightSourceRadius * atlasRadiusScale,
                ShadowMinPenumbra * cascadeScale * atlasRadiusScale,
                ShadowMaxPenumbra * cascadeScale * atlasRadiusScale,
                ShadowVogelTapCount) * contact;
        }

        if (fallbackMode == 2)
            return contact;
        return XRENGINE_SampleShadowMapArrayFiltered(
            ShadowMapArray,
            fragCoord,
            float(cascadeIndex),
            bias,
            ShadowBlockerSamples,
            ShadowFilterSamples,
            filterRadius,
            ShadowBlockerSearchRadius * cascadeScale,
            SoftShadowMode,
            LightSourceRadius,
            ShadowMinPenumbra * cascadeScale,
            ShadowMaxPenumbra * cascadeScale,
            ShadowVogelTapCount) * contact;
    }

    return XRENGINE_SampleShadowMapArrayFiltered(
        ShadowMapArray,
        fragCoord,
        float(cascadeIndex),
        bias,
        ShadowBlockerSamples,
        ShadowFilterSamples,
        filterRadius,
        ShadowBlockerSearchRadius * cascadeScale,
        SoftShadowMode,
        LightSourceRadius,
        ShadowMinPenumbra * cascadeScale,
        ShadowMaxPenumbra * cascadeScale,
        ShadowVogelTapCount) * contact;
}

float XRENGINE_ReadDirectionalContactShadowOnly(vec3 fragPos, vec3 normal, float diffuseFactor)
{
    if (!EnableContactShadows || !ForwardContactShadowsEnabled || DirLightCount <= 0)
        return 1.0;

    float bias = XRENGINE_GetShadowBias(diffuseFactor);
    float viewDepth = XRENGINE_ViewDepthFromWorldPos(fragPos);
    int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
        ContactShadowSamples,
        viewDepth,
        ContactShadowDistance);

    return XRENGINE_SampleForwardContactShadowScreenSpace(
        fragPos,
        normal,
        normalize(-DirectionalLights[0].Direction),
        ShadowBiasMax,
        bias,
        ContactShadowDistance,
        contactSampleCount,
        ContactShadowThickness,
        ContactShadowFadeStart,
        ContactShadowFadeEnd,
        ContactShadowNormalOffset,
        ContactShadowJitterStrength,
        viewDepth);
}

// Shadow map reading for primary directional light (uses standalone ShadowMap sampler)
float XRENGINE_ReadShadowMapDir(vec3 fragPos, vec3 normal, float diffuseFactor)
{
    if (!ShadowMapEnabled)
        return XRENGINE_ReadDirectionalContactShadowOnly(fragPos, normal, diffuseFactor);

    if (UseCascadedDirectionalShadows && EnableCascadedShadows && DirLightCount > 0)
    {
        DirLight primaryLight = DirectionalLights[0];
        if (primaryLight.CascadeCount > 0)
        {
            float viewDepth = XRENGINE_ViewDepthFromWorldPos(fragPos);
            int cascadeCount = min(primaryLight.CascadeCount, XRENGINE_MAX_CASCADES);

            for (int i = 0; i < cascadeCount; ++i)
            {
                float splitFar = primaryLight.CascadeSplits[i];
                bool isLast = (i == cascadeCount - 1);

                if (viewDepth <= splitFar || isLast)
                {
                    // Clamp UV for the last cascade so shadows persist beyond its far split.
                    bool clampToEdge = isLast && viewDepth > splitFar;
                    float shadow0 = XRENGINE_ReadCascadeShadowMapDir(fragPos, normal, diffuseFactor, i, clampToEdge);
                    if (shadow0 < 0.0) shadow0 = 1.0;

                    // Blend toward next cascade across the overlap zone.
                    if (!isLast)
                    {
                        float blendWidth = primaryLight.CascadeBlendWidths[i];
                        if (blendWidth > 0.0 && viewDepth > splitFar - blendWidth)
                        {
                            float t = clamp((viewDepth - (splitFar - blendWidth)) / blendWidth, 0.0, 1.0);
                            float shadow1 = XRENGINE_ReadCascadeShadowMapDir(fragPos, normal, diffuseFactor, i + 1, false);
                            if (shadow1 < 0.0) shadow1 = shadow0;
                            return mix(shadow0, shadow1, t);
                        }
                    }

                    return shadow0;
                }
            }
        }
    }

    // Fallback: standalone (non-cascade) shadow map.
    mat4 lightMatrix = PrimaryDirLightWorldToLightProjMatrix * inverse(PrimaryDirLightWorldToLightInvViewMatrix);
    vec3 offsetPosWS = fragPos + normal * ShadowBiasMax;
    vec3 fragCoord = XRENGINE_ProjectShadowCoord(lightMatrix, offsetPosWS);

    // Outside shadow map bounds: treat as fully lit
    if (!XRENGINE_ShadowCoordInBounds(fragCoord))
        return XRENGINE_ReadDirectionalContactShadowOnly(fragPos, normal, diffuseFactor);

    float bias = XRENGINE_GetShadowBias(diffuseFactor);
    float viewDepth = XRENGINE_ViewDepthFromWorldPos(fragPos);
    int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
        ContactShadowSamples,
        viewDepth,
        ContactShadowDistance);
    vec3 lightDirWS = normalize(-DirectionalLights[0].Direction);
    float contact = 1.0;
    if (EnableContactShadows)
    {
        contact = ForwardContactShadowsEnabled
            ? XRENGINE_SampleForwardContactShadowScreenSpace(
                fragPos,
                normal,
                lightDirWS,
                ShadowBiasMax,
                bias,
                ContactShadowDistance,
                contactSampleCount,
                ContactShadowThickness,
                ContactShadowFadeStart,
                ContactShadowFadeEnd,
                ContactShadowNormalOffset,
                ContactShadowJitterStrength,
                viewDepth)
            : XRENGINE_SampleContactShadow2D(
                ShadowMap,
                lightMatrix,
                fragPos,
                normal,
                lightDirWS,
                ShadowBiasMax,
                bias,
                ContactShadowDistance,
                contactSampleCount,
                ContactShadowThickness,
                ContactShadowFadeStart,
                ContactShadowFadeEnd,
                ContactShadowNormalOffset,
                ContactShadowJitterStrength,
                viewDepth);
    }

    if (DirectionalShadowAtlasEnabled)
    {
        ivec4 atlasI0 = DirectionalShadowAtlasPacked0[0];
        bool atlasEnabled = atlasI0.x != 0 && atlasI0.y >= 0 && atlasI0.y < XRENGINE_MAX_FORWARD_SPOT_ATLAS_PAGES;
        int fallbackMode = atlasI0.z;
        if (atlasEnabled)
        {
            vec4 atlasUvScaleBias = DirectionalShadowAtlasParams0[0];
            vec2 atlasUv = fragCoord.xy * atlasUvScaleBias.xy + atlasUvScaleBias.zw;
            float atlasRadiusScale = max(atlasUvScaleBias.x, atlasUvScaleBias.y);
            return XRENGINE_SampleDirectionalAtlasPage(
                atlasI0.y,
                vec3(atlasUv, fragCoord.z),
                bias,
                ShadowBlockerSamples,
                ShadowFilterSamples,
                ShadowFilterRadius * atlasRadiusScale,
                ShadowBlockerSearchRadius * atlasRadiusScale,
                SoftShadowMode,
                LightSourceRadius * atlasRadiusScale,
                ShadowMinPenumbra * atlasRadiusScale,
                ShadowMaxPenumbra * atlasRadiusScale,
                ShadowVogelTapCount) * contact;
        }

        if (fallbackMode == 1 || fallbackMode == 2 || fallbackMode == 4)
            return contact;
    }

    return XRENGINE_SampleShadowMapFiltered(
        ShadowMap,
        fragCoord,
        bias,
        ShadowBlockerSamples,
        ShadowFilterSamples,
        ShadowFilterRadius,
        ShadowBlockerSearchRadius,
        SoftShadowMode,
        LightSourceRadius,
        ShadowMinPenumbra,
        ShadowMaxPenumbra,
        ShadowVogelTapCount) * contact;
}

vec3 XRENGINE_CalculateDirectPbrLight(vec3 lightColor, float diffuseIntensity, vec3 lightDirection, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0, float attenuation)
{
    vec3 viewDir = normalize(XRENGINE_GetForwardCameraPosition() - fragPos);
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

// Forward+ light lists vary per fragment. OpenGL sampler arrays require
// dynamically uniform indices, so point shadow cubemaps dispatch through
// fixed-slot branches instead of indexing the sampler array by runtime slot.
float XRENGINE_GetPointShadowSampleRadiusForSlot(int shadowSlot, float filterRadius)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_GetPointShadowSampleRadius(PointLightShadowMaps[0], filterRadius);
    case 1:
        return XRENGINE_GetPointShadowSampleRadius(PointLightShadowMaps[1], filterRadius);
    case 2:
        return XRENGINE_GetPointShadowSampleRadius(PointLightShadowMaps[2], filterRadius);
    case 3:
        return XRENGINE_GetPointShadowSampleRadius(PointLightShadowMaps[3], filterRadius);
    default:
        return filterRadius;
    }
}

float XRENGINE_GetPointShadowFaceSizeForSlot(int shadowSlot)
{
    switch (shadowSlot)
    {
    case 0:
        return max(float(textureSize(PointLightShadowMaps[0], 0).x), 1.0);
    case 1:
        return max(float(textureSize(PointLightShadowMaps[1], 0).x), 1.0);
    case 2:
        return max(float(textureSize(PointLightShadowMaps[2], 0).x), 1.0);
    case 3:
        return max(float(textureSize(PointLightShadowMaps[3], 0).x), 1.0);
    default:
        return 1.0;
    }
}

float XRENGINE_GetPointShadowTexelRelativeBiasForSlot(int shadowSlot, float NoL)
{
    // Per cube-map texel, the stored radial depth varies by lightDist*tan(theta)*texelAngularSize,
    // where theta is the angle between the surface normal and the direction-to-light. The previous
    // (1 + 3*slope^2) factor undershoots at grazing angles (NoL < ~0.4), which lets the cubemap
    // texel grid show through as radial herringbone bristles around the lit spot. tan(theta) is
    // the geometrically correct multiplier; the +1 keeps a base texel-width margin at normal
    // incidence and the 2x on tan(theta) absorbs sub-texel sampling phase + R16f quantisation.
    float faceSize = XRENGINE_GetPointShadowFaceSizeForSlot(shadowSlot);
    float NoLSafe = max(NoL, 0.05);
    float tanTheta = sqrt(max(1.0 - NoLSafe * NoLSafe, 0.0)) / NoLSafe;
    return (2.0 / faceSize) * (1.0 + 2.0 * tanTheta);
}

float XRENGINE_SamplePointContactShadowCubeSlot(
    int shadowSlot,
    vec3 fragPosWS,
    vec3 normalWS,
    vec3 lightPosWS,
    float receiverOffset,
    float compareBias,
    float farPlaneDist,
    float contactDistance,
    int contactSamples,
    float contactThickness,
    float contactFadeStart,
    float contactFadeEnd,
    float contactNormalOffset,
    float jitterStrength,
    float viewDepth)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_SampleContactShadowCube(PointLightShadowMaps[0], fragPosWS, normalWS, lightPosWS, receiverOffset, compareBias, farPlaneDist, contactDistance, contactSamples, contactThickness, contactFadeStart, contactFadeEnd, contactNormalOffset, jitterStrength, viewDepth);
    case 1:
        return XRENGINE_SampleContactShadowCube(PointLightShadowMaps[1], fragPosWS, normalWS, lightPosWS, receiverOffset, compareBias, farPlaneDist, contactDistance, contactSamples, contactThickness, contactFadeStart, contactFadeEnd, contactNormalOffset, jitterStrength, viewDepth);
    case 2:
        return XRENGINE_SampleContactShadowCube(PointLightShadowMaps[2], fragPosWS, normalWS, lightPosWS, receiverOffset, compareBias, farPlaneDist, contactDistance, contactSamples, contactThickness, contactFadeStart, contactFadeEnd, contactNormalOffset, jitterStrength, viewDepth);
    case 3:
        return XRENGINE_SampleContactShadowCube(PointLightShadowMaps[3], fragPosWS, normalWS, lightPosWS, receiverOffset, compareBias, farPlaneDist, contactDistance, contactSamples, contactThickness, contactFadeStart, contactFadeEnd, contactNormalOffset, jitterStrength, viewDepth);
    default:
        return 1.0;
    }
}

float XRENGINE_SamplePointShadowCubeSlot(
    int shadowSlot,
    vec3 shadowDir,
    float compareDepth,
    float bias,
    float farPlaneDist,
    float sampleRadius,
    float blockerSearchRadius,
    int blockerSamples,
    int filterSamples,
    int softShadowMode,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra,
    int vogelTapCount)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_SampleShadowCubeFiltered(PointLightShadowMaps[0], shadowDir, compareDepth, bias, farPlaneDist, sampleRadius, blockerSearchRadius, blockerSamples, filterSamples, softShadowMode, lightSourceRadius, minPenumbra, maxPenumbra, vogelTapCount);
    case 1:
        return XRENGINE_SampleShadowCubeFiltered(PointLightShadowMaps[1], shadowDir, compareDepth, bias, farPlaneDist, sampleRadius, blockerSearchRadius, blockerSamples, filterSamples, softShadowMode, lightSourceRadius, minPenumbra, maxPenumbra, vogelTapCount);
    case 2:
        return XRENGINE_SampleShadowCubeFiltered(PointLightShadowMaps[2], shadowDir, compareDepth, bias, farPlaneDist, sampleRadius, blockerSearchRadius, blockerSamples, filterSamples, softShadowMode, lightSourceRadius, minPenumbra, maxPenumbra, vogelTapCount);
    case 3:
        return XRENGINE_SampleShadowCubeFiltered(PointLightShadowMaps[3], shadowDir, compareDepth, bias, farPlaneDist, sampleRadius, blockerSearchRadius, blockerSamples, filterSamples, softShadowMode, lightSourceRadius, minPenumbra, maxPenumbra, vogelTapCount);
    default:
        return 1.0;
    }
}

float XRENGINE_ReadPointShadowCenterDepthForSlot(int shadowSlot, vec3 shadowDir, float farPlaneDist)
{
    switch (shadowSlot)
    {
    case 0:
        return texture(PointLightShadowMaps[0], shadowDir).r * farPlaneDist;
    case 1:
        return texture(PointLightShadowMaps[1], shadowDir).r * farPlaneDist;
    case 2:
        return texture(PointLightShadowMaps[2], shadowDir).r * farPlaneDist;
    case 3:
        return texture(PointLightShadowMaps[3], shadowDir).r * farPlaneDist;
    default:
        return farPlaneDist;
    }
}

float XRENGINE_SampleSpotContactShadow2DSlot(
    int shadowSlot,
    mat4 worldToLightSpaceProjMatrix,
    vec3 fragPosWS,
    vec3 normalWS,
    vec3 lightDirWS,
    float receiverOffset,
    float compareBias,
    float contactDistance,
    int contactSamples,
    float contactThickness,
    float contactFadeStart,
    float contactFadeEnd,
    float contactNormalOffset,
    float jitterStrength,
    float viewDepth)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_SampleContactShadow2D(SpotLightShadowMaps[0], worldToLightSpaceProjMatrix, fragPosWS, normalWS, lightDirWS, receiverOffset, compareBias, contactDistance, contactSamples, contactThickness, contactFadeStart, contactFadeEnd, contactNormalOffset, jitterStrength, viewDepth);
    case 1:
        return XRENGINE_SampleContactShadow2D(SpotLightShadowMaps[1], worldToLightSpaceProjMatrix, fragPosWS, normalWS, lightDirWS, receiverOffset, compareBias, contactDistance, contactSamples, contactThickness, contactFadeStart, contactFadeEnd, contactNormalOffset, jitterStrength, viewDepth);
    case 2:
        return XRENGINE_SampleContactShadow2D(SpotLightShadowMaps[2], worldToLightSpaceProjMatrix, fragPosWS, normalWS, lightDirWS, receiverOffset, compareBias, contactDistance, contactSamples, contactThickness, contactFadeStart, contactFadeEnd, contactNormalOffset, jitterStrength, viewDepth);
    case 3:
        return XRENGINE_SampleContactShadow2D(SpotLightShadowMaps[3], worldToLightSpaceProjMatrix, fragPosWS, normalWS, lightDirWS, receiverOffset, compareBias, contactDistance, contactSamples, contactThickness, contactFadeStart, contactFadeEnd, contactNormalOffset, jitterStrength, viewDepth);
    default:
        return 1.0;
    }
}

float XRENGINE_SampleSpotShadowMoment2DSlot(
    int shadowSlot,
    vec2 uv,
    float receiverDepth,
    int encoding,
    float minVariance,
    float lightBleedReduction,
    float positiveExponent,
    float negativeExponent,
    float mipBias)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_SampleShadowMoment2D(SpotLightShadowMaps[0], uv, receiverDepth, encoding, minVariance, lightBleedReduction, positiveExponent, negativeExponent, mipBias);
    case 1:
        return XRENGINE_SampleShadowMoment2D(SpotLightShadowMaps[1], uv, receiverDepth, encoding, minVariance, lightBleedReduction, positiveExponent, negativeExponent, mipBias);
    case 2:
        return XRENGINE_SampleShadowMoment2D(SpotLightShadowMaps[2], uv, receiverDepth, encoding, minVariance, lightBleedReduction, positiveExponent, negativeExponent, mipBias);
    case 3:
        return XRENGINE_SampleShadowMoment2D(SpotLightShadowMaps[3], uv, receiverDepth, encoding, minVariance, lightBleedReduction, positiveExponent, negativeExponent, mipBias);
    default:
        return 1.0;
    }
}

float XRENGINE_SampleSpotShadowDepth2DSlot(
    int shadowSlot,
    vec3 shadowCoord,
    float bias,
    int blockerSamples,
    int filterSamples,
    float filterRadius,
    float blockerSearchRadius,
    int softShadowMode,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra,
    int vogelTapCount)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_SampleShadowMapFiltered(SpotLightShadowMaps[0], shadowCoord, bias, blockerSamples, filterSamples, filterRadius, blockerSearchRadius, softShadowMode, lightSourceRadius, minPenumbra, maxPenumbra, vogelTapCount);
    case 1:
        return XRENGINE_SampleShadowMapFiltered(SpotLightShadowMaps[1], shadowCoord, bias, blockerSamples, filterSamples, filterRadius, blockerSearchRadius, softShadowMode, lightSourceRadius, minPenumbra, maxPenumbra, vogelTapCount);
    case 2:
        return XRENGINE_SampleShadowMapFiltered(SpotLightShadowMaps[2], shadowCoord, bias, blockerSamples, filterSamples, filterRadius, blockerSearchRadius, softShadowMode, lightSourceRadius, minPenumbra, maxPenumbra, vogelTapCount);
    case 3:
        return XRENGINE_SampleShadowMapFiltered(SpotLightShadowMaps[3], shadowCoord, bias, blockerSamples, filterSamples, filterRadius, blockerSearchRadius, softShadowMode, lightSourceRadius, minPenumbra, maxPenumbra, vogelTapCount);
    default:
        return 1.0;
    }
}

float XRENGINE_EstimateSpotShadowMomentMargin2DSlot(
    int shadowSlot,
    vec2 uv,
    float receiverDepth,
    int encoding,
    float positiveExponent,
    float negativeExponent)
{
    switch (shadowSlot)
    {
    case 0:
        return XRENGINE_EstimateShadowMomentMargin(SpotLightShadowMaps[0], uv, receiverDepth, encoding, positiveExponent, negativeExponent);
    case 1:
        return XRENGINE_EstimateShadowMomentMargin(SpotLightShadowMaps[1], uv, receiverDepth, encoding, positiveExponent, negativeExponent);
    case 2:
        return XRENGINE_EstimateShadowMomentMargin(SpotLightShadowMaps[2], uv, receiverDepth, encoding, positiveExponent, negativeExponent);
    case 3:
        return XRENGINE_EstimateShadowMomentMargin(SpotLightShadowMaps[3], uv, receiverDepth, encoding, positiveExponent, negativeExponent);
    default:
        return 1.0;
    }
}

float XRENGINE_ReadSpotShadowCenterDepthForSlot(int shadowSlot, vec2 uv)
{
    switch (shadowSlot)
    {
    case 0:
        return texture(SpotLightShadowMaps[0], uv).r;
    case 1:
        return texture(SpotLightShadowMaps[1], uv).r;
    case 2:
        return texture(SpotLightShadowMaps[2], uv).r;
    case 3:
        return texture(SpotLightShadowMaps[3], uv).r;
    default:
        return 1.0;
    }
}

float XRENGINE_ReadPointContactShadowOnly(int lightIndex, PointLight light, vec3 normal, vec3 fragPos)
{
    if (!ForwardContactShadowsEnabled || lightIndex < 0 || lightIndex >= PointLightCount || lightIndex >= PointLightShadows.length())
        return 1.0;

    ForwardPointShadowData shadowData = PointLightShadows[lightIndex];
    ivec4 shadowI1 = shadowData.Packed1;
    if (shadowI1.z == 0)
        return 1.0;

    vec4 shadowF2 = shadowData.Params2;
    vec4 shadowF3 = shadowData.Params3;
    vec4 shadowF4 = shadowData.Params4;
    float contactDistance = shadowF2.w;
    float viewDepth = XRENGINE_ViewDepthFromWorldPos(fragPos);
    int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
        shadowI1.w,
        viewDepth,
        contactDistance);

    return XRENGINE_SampleForwardContactShadowScreenSpace(
        fragPos,
        normal,
        normalize(light.Position - fragPos),
        shadowF3.w,
        contactDistance * 0.001,
        contactDistance,
        contactSampleCount,
        shadowF3.x,
        shadowF3.y,
        shadowF3.z,
        shadowF3.w,
        shadowF4.x,
        viewDepth);
}

float XRENGINE_ReadShadowMapPoint(int lightIndex, PointLight light, vec3 normal, vec3 fragPos)
{
    if (lightIndex < 0 || lightIndex >= PointLightCount || lightIndex >= PointLightShadows.length())
        return 1.0;

    ForwardPointShadowData shadowData = PointLightShadows[lightIndex];
    ivec4 shadowI0 = shadowData.Packed0;
    ivec4 shadowI1 = shadowData.Packed1;
    vec4 shadowF0 = shadowData.Params0;
    vec4 shadowF1 = shadowData.Params1;
    vec4 shadowF2 = shadowData.Params2;
    vec4 shadowF3 = shadowData.Params3;
    vec4 shadowF4 = shadowData.Params4;

    int shadowSlot = shadowI0.x;
    int debugMode = shadowI1.y;
    if (shadowSlot < 0 || shadowSlot >= XRENGINE_MAX_FORWARD_POINT_SHADOW_SLOTS)
    {
        XRENGINE_TrySetForwardShadowDebug(debugMode, 1.0, 1.0);
        return 1.0;
    }

    float NoL = max(dot(normal, normalize(light.Position - fragPos)), 0.0);
    float userBias = XRENGINE_GetLocalShadowBias(
        NoL,
        shadowF0.z,
        shadowF0.w,
        shadowF1.x,
        shadowF1.y);
    vec3 offsetPosWS = fragPos + normal * shadowF1.y;
    vec3 fragToLight = offsetPosWS - light.Position;
    float lightDist = length(fragToLight);
    float nearPlaneDist = max(shadowF0.x, 0.0);
    float farPlaneDist = shadowF0.y;
    if (lightDist >= farPlaneDist)
        return 1.0;
    // The cubemap shadow cameras clip everything inside the near plane.
    // Geometry crossing that plane can create a synthetic blocker shell near the light,
    // so receivers in that blind zone should be treated as unshadowed.
    if (lightDist <= nearPlaneDist + shadowF1.y)
    {
        if (debugMode != 0)
            XRENGINE_TrySetForwardShadowDebug(debugMode, 1.0, nearPlaneDist / max(farPlaneDist, 0.001));
        return 1.0;
    }
    // Point-light shadow cubemaps compare radial R16f depth on cube faces. Use
    // the same relative threshold shape as the deferred point-light path so
    // forward receivers do not self-shadow on wide/grazing cube-map texels.
    float texelRel = XRENGINE_GetPointShadowTexelRelativeBiasForSlot(shadowSlot, NoL);
    float userRel = userBias / max(lightDist, 0.001);
    float r16fRel = 1.0 / 512.0;
    float relThreshold = max(texelRel, max(userRel, r16fRel));
    float compareBias = lightDist * relThreshold;
    float biasedLightDist = lightDist - compareBias;
    float sampleRadius = XRENGINE_GetPointShadowSampleRadiusForSlot(shadowSlot, shadowF1.z);
    float blockerSearchRadius = XRENGINE_GetPointShadowSampleRadiusForSlot(shadowSlot, shadowF1.w);
    float minPenumbra = XRENGINE_GetPointShadowSampleRadiusForSlot(shadowSlot, shadowF2.x);
    float maxPenumbra = XRENGINE_GetPointShadowSampleRadiusForSlot(shadowSlot, shadowF2.y);
    float viewDepth = XRENGINE_ViewDepthFromWorldPos(fragPos);
    int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
        shadowI1.w,
        viewDepth,
        shadowF2.w);
    float contact = 1.0;
    if (shadowI1.z != 0)
    {
        contact = ForwardContactShadowsEnabled
            ? XRENGINE_SampleForwardContactShadowScreenSpace(
                fragPos,
                normal,
                normalize(light.Position - fragPos),
                shadowF1.y,
                compareBias,
                shadowF2.w,
                contactSampleCount,
                shadowF3.x,
                shadowF3.y,
                shadowF3.z,
                shadowF3.w,
                shadowF4.x,
                viewDepth)
            : XRENGINE_SamplePointContactShadowCubeSlot(
                shadowSlot,
                fragPos,
                normal,
                light.Position,
                shadowF1.y,
                compareBias,
                farPlaneDist,
                shadowF2.w,
                contactSampleCount,
                shadowF3.x,
                shadowF3.y,
                shadowF3.z,
                shadowF3.w,
                shadowF4.x,
                viewDepth);
    }

    // The cubemap supplies large-scale visibility; contact shadows add only the
    // short-range receiver/detail term so cubemap debugging can still isolate
    // the filtered shadow value before this multiplier.
    float shadow = XRENGINE_SamplePointShadowCubeSlot(
        shadowSlot,
        fragToLight,
        biasedLightDist,
        0.0,
        farPlaneDist,
        sampleRadius,
        blockerSearchRadius,
        shadowI0.z,
        shadowI0.y,
        shadowI1.x,
        shadowF2.z,
        minPenumbra,
        maxPenumbra,
        shadowI0.w) * contact;

    if (debugMode != 0)
    {
        float centerDepth = XRENGINE_ReadPointShadowCenterDepthForSlot(shadowSlot, normalize(fragToLight), farPlaneDist);
        float margin = (centerDepth - biasedLightDist) / max(farPlaneDist, 0.001);
        XRENGINE_TrySetForwardShadowDebug(debugMode, shadow, margin);
    }

    return shadow;
}

float XRENGINE_LinearizeSpotShadowDepth01(float depth, float nearZ, float farZ)
{
    float n = max(nearZ, 0.0001);
    float f = max(farZ, n + 0.0001);
    float z = depth * 2.0 - 1.0;
    float linearZ = (2.0 * n * f) / (f + n - z * (f - n));
    return clamp((linearZ - n) / (f - n), 0.0, 1.0);
}

float XRENGINE_SampleSpotAtlasPage(
    int pageIndex,
    vec3 atlasCoord,
    float receiverPerspectiveDepth,
    float bias,
    int blockerSamples,
    int filterSamples,
    float filterRadius,
    float blockerSearchRadius,
    int softMode,
    float lightSourceRadius,
    float minPenumbra,
    float maxPenumbra,
    int vogelTapCount,
    float nearZ,
    float farZ)
{
    if (pageIndex == 0)
    {
        return XRENGINE_SampleLinearDepthShadowMapFilteredAsPerspective(
            SpotLightShadowAtlasPages[0],
            atlasCoord,
            receiverPerspectiveDepth,
            bias,
            blockerSamples,
            filterSamples,
            filterRadius,
            blockerSearchRadius,
            softMode,
            lightSourceRadius,
            minPenumbra,
            maxPenumbra,
            vogelTapCount,
            nearZ,
            farZ);
    }

    if (pageIndex == 1)
    {
        return XRENGINE_SampleLinearDepthShadowMapFilteredAsPerspective(
            SpotLightShadowAtlasPages[1],
            atlasCoord,
            receiverPerspectiveDepth,
            bias,
            blockerSamples,
            filterSamples,
            filterRadius,
            blockerSearchRadius,
            softMode,
            lightSourceRadius,
            minPenumbra,
            maxPenumbra,
            vogelTapCount,
            nearZ,
            farZ);
    }

    return 1.0;
}

float XRENGINE_ReadSpotAtlasCenterDepth(int pageIndex, vec2 uv)
{
    if (pageIndex == 0)
        return texture(SpotLightShadowAtlasPages[0], uv).r;
    if (pageIndex == 1)
        return texture(SpotLightShadowAtlasPages[1], uv).r;
    return 1.0;
}

float XRENGINE_ReadSpotContactShadowOnly(int lightIndex, vec3 lightDir, vec3 normal, vec3 fragPos)
{
    if (!ForwardContactShadowsEnabled || lightIndex < 0 || lightIndex >= SpotLightCount || lightIndex >= SpotLightShadows.length())
        return 1.0;

    ForwardSpotShadowData shadowData = SpotLightShadows[lightIndex];
    ivec4 shadowI1 = shadowData.Packed1;
    if (shadowI1.z == 0)
        return 1.0;

    vec4 shadowF2 = shadowData.Params2;
    vec4 shadowF3 = shadowData.Params3;
    float contactDistance = shadowF2.y;
    float viewDepth = XRENGINE_ViewDepthFromWorldPos(fragPos);
    int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
        shadowI1.w,
        viewDepth,
        contactDistance);

    return XRENGINE_SampleForwardContactShadowScreenSpace(
        fragPos,
        normal,
        lightDir,
        shadowF3.y,
        contactDistance * 0.001,
        contactDistance,
        contactSampleCount,
        shadowF2.z,
        shadowF2.w,
        shadowF3.x,
        shadowF3.y,
        shadowF3.z,
        viewDepth);
}

float XRENGINE_ReadShadowMapSpot(int lightIndex, SpotLight light, vec3 normal, vec3 fragPos, vec3 lightDir)
{
    if (lightIndex < 0 || lightIndex >= SpotLightCount || lightIndex >= SpotLightShadows.length())
        return 1.0;

    ForwardSpotShadowData shadowData = SpotLightShadows[lightIndex];
    ivec4 shadowI0 = shadowData.Packed0;
    ivec4 shadowI1 = shadowData.Packed1;
    vec4 shadowF0 = shadowData.Params0;
    vec4 shadowF1 = shadowData.Params1;
    vec4 shadowF2 = shadowData.Params2;
    vec4 shadowF3 = shadowData.Params3;
    ivec4 shadowI2 = shadowData.Packed2;
    vec4 shadowF4 = shadowData.Params4;
    vec4 shadowF5 = shadowData.Params5;
    ivec4 atlasI0 = shadowData.AtlasPacked0;
    vec4 atlasUvScaleBias = shadowData.AtlasParams0;
    vec4 atlasDepthParams = shadowData.AtlasParams1;

    int shadowSlot = shadowI0.x;
    int debugMode = shadowI1.y;
    bool legacyShadowSlotValid = shadowSlot >= 0 && shadowSlot < XRENGINE_MAX_FORWARD_SPOT_SHADOW_SLOTS;
    bool atlasEnabled = atlasI0.x != 0 && atlasI0.y >= 0 && atlasI0.y < XRENGINE_MAX_FORWARD_SPOT_ATLAS_PAGES;
    int fallbackMode = atlasI0.z;

    float NoL = max(dot(normal, lightDir), 0.0);
    float bias = XRENGINE_GetLocalShadowBias(
        NoL,
        shadowF0.x,
        shadowF0.y,
        shadowF0.z,
        shadowF0.w);
    vec3 offsetPosWS = fragPos + normal * shadowF0.w;
    vec3 fragCoord = XRENGINE_ProjectShadowCoord(light.Base.Base.WorldToLightSpaceProjMatrix, offsetPosWS);
    if (!XRENGINE_ShadowCoordInBounds(fragCoord))
        return 1.0;

    float viewDepth = XRENGINE_ViewDepthFromWorldPos(fragPos);
    int contactSampleCount = XRENGINE_ResolveContactShadowSampleCount(
        shadowI1.w,
        viewDepth,
        shadowF2.y);
    float contact = 1.0;
    if (shadowI1.z != 0)
    {
        if (ForwardContactShadowsEnabled)
        {
            contact = XRENGINE_SampleForwardContactShadowScreenSpace(
                fragPos,
                normal,
                lightDir,
                shadowF0.w,
                bias,
                shadowF2.y,
                contactSampleCount,
                shadowF2.z,
                shadowF2.w,
                shadowF3.x,
                shadowF3.y,
                shadowF3.z,
                viewDepth);
        }
        else if (!atlasEnabled && legacyShadowSlotValid)
        {
            contact = XRENGINE_SampleSpotContactShadow2DSlot(
                shadowSlot,
                light.Base.Base.WorldToLightSpaceProjMatrix,
                fragPos,
                normal,
                lightDir,
                shadowF0.w,
                bias,
                shadowF2.y,
                contactSampleCount,
                shadowF2.z,
                shadowF2.w,
                shadowF3.x,
                shadowF3.y,
                shadowF3.z,
                viewDepth);
        }
    }

    if (atlasEnabled)
    {
        vec2 atlasUv = fragCoord.xy * atlasUvScaleBias.xy + atlasUvScaleBias.zw;
        float atlasRadiusScale = max(atlasUvScaleBias.x, atlasUvScaleBias.y);
        float atlasDepth = XRENGINE_LinearizeSpotShadowDepth01(
            fragCoord.z,
            atlasDepthParams.x,
            atlasDepthParams.y);
        float atlasFilterRadius = shadowF1.x * atlasRadiusScale;
        float atlasBlockerRadius = shadowF1.y * atlasRadiusScale;

        float shadow = XRENGINE_SampleSpotAtlasPage(
            atlasI0.y,
            vec3(atlasUv, atlasDepth),
            fragCoord.z,
            bias,
            shadowI0.z,
            shadowI0.y,
            atlasFilterRadius,
            atlasBlockerRadius,
            shadowI1.x,
            shadowF2.x * atlasRadiusScale,
            shadowF1.z * atlasRadiusScale,
            shadowF1.w * atlasRadiusScale,
            shadowI0.w,
            atlasDepthParams.x,
            atlasDepthParams.y) * contact;

        if (debugMode != 0)
        {
            float centerDepth = XRENGINE_LinearDepth01ToPerspectiveDepth(
                XRENGINE_ReadSpotAtlasCenterDepth(atlasI0.y, atlasUv),
                atlasDepthParams.x,
                atlasDepthParams.y);
            float margin = centerDepth - (fragCoord.z - bias);
            XRENGINE_TrySetForwardShadowDebug(debugMode, shadow, margin);
        }

        return shadow;
    }

    if (!legacyShadowSlotValid)
    {
        float fallback = fallbackMode == 2 ? contact : 1.0;
        XRENGINE_TrySetForwardShadowDebug(debugMode, fallback, 1.0);
        return fallback;
    }

    int encoding = shadowI2.x;
    float shadow = 1.0;
    if (encoding != XRENGINE_SHADOW_ENCODING_DEPTH)
    {
        float receiverDepth = XRENGINE_LinearizeShadowDepth01(fragCoord.z, shadowF5.x, shadowF5.y);
        float momentReceiverDepth = clamp(receiverDepth - min(bias, 0.01), 0.0, 1.0);
        shadow = XRENGINE_SampleSpotShadowMoment2DSlot(
            shadowSlot,
            fragCoord.xy,
            momentReceiverDepth,
            encoding,
            shadowF4.x,
            shadowF4.y,
            shadowF4.z,
            shadowF4.w,
            shadowF5.z) * contact;
    }
    else
    {
        shadow = XRENGINE_SampleSpotShadowDepth2DSlot(
            shadowSlot,
            fragCoord,
            bias,
            shadowI0.z,
            shadowI0.y,
            shadowF1.x,
            shadowF1.y,
            shadowI1.x,
            shadowF2.x,
            shadowF1.z,
            shadowF1.w,
            shadowI0.w) * contact;
    }

    if (debugMode != 0)
    {
        float margin = encoding != XRENGINE_SHADOW_ENCODING_DEPTH
            ? XRENGINE_EstimateSpotShadowMomentMargin2DSlot(
                shadowSlot,
                fragCoord.xy,
                clamp(XRENGINE_LinearizeShadowDepth01(fragCoord.z, shadowF5.x, shadowF5.y) - min(bias, 0.01), 0.0, 1.0),
                encoding,
                shadowF4.z,
                shadowF4.w)
            : XRENGINE_ReadSpotShadowCenterDepthForSlot(shadowSlot, fragCoord.xy) - (fragCoord.z - bias);
        XRENGINE_TrySetForwardShadowDebug(debugMode, shadow, margin);
    }

    return shadow;
}

vec3 XRENGINE_CalcPointLight(int lightIndex, PointLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0)
{
    vec3 lightVector = light.Position - fragPos;
    float attenuation = XRENGINE_Attenuate(length(lightVector), light.Radius) * light.Brightness;
    float shadow = XRENGINE_ReadShadowMapPoint(lightIndex, light, normal, fragPos);
    return XRENGINE_CalculateDirectPbrLight(light.Base.Color, light.Base.DiffuseIntensity, normalize(lightVector), normal, fragPos, albedo, rms, F0, attenuation) * shadow;
}

vec3 XRENGINE_CalcSpotLight(int lightIndex, SpotLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0)
{
    vec3 lightVector = light.Base.Position - fragPos;
    vec3 lightDir = normalize(lightVector);
    float clampedCosine = max(0.0, dot(-lightDir, normalize(light.Direction)));
    float spotEffect = smoothstep(light.OuterCutoff, light.InnerCutoff, clampedCosine);
    float spotAttn = pow(clampedCosine, light.Exponent);
    float distAttn = XRENGINE_Attenuate(length(lightVector), light.Base.Radius) * light.Base.Brightness;
    float shadow = XRENGINE_ReadShadowMapSpot(lightIndex, light, normal, fragPos, lightDir);
    return spotEffect * spotAttn * XRENGINE_CalculateDirectPbrLight(light.Base.Base.Color, light.Base.Base.DiffuseIntensity, lightDir, normal, fragPos, albedo, rms, F0, distAttn) * shadow;
}

vec3 XRENGINE_CalcForwardPlusColor(vec3 lightColor, float diffuseIntensity, vec3 lightDirection, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0, float attenuation)
{
    return XRENGINE_CalculateDirectPbrLight(lightColor, diffuseIntensity, lightDirection, normal, fragPos, albedo, rms, F0, attenuation);
}

vec3 XRENGINE_CalcForwardPlusPointLight(ForwardPlusLocalLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0)
{
    vec3 lightVector = light.PositionWS.xyz - fragPos;
    float attenuation = XRENGINE_Attenuate(length(lightVector), light.Params.x) * max(light.Params.y, 0.0001);
    int sourceIndex = int(light.Params.w + 0.5);
    float shadow = (sourceIndex >= 0 && sourceIndex < PointLightCount)
        ? XRENGINE_ReadShadowMapPoint(sourceIndex, PointLights[sourceIndex], normal, fragPos)
        : 1.0;
    return XRENGINE_CalcForwardPlusColor(light.Color_Type.xyz, light.Params.z, normalize(lightVector), normal, fragPos, albedo, rms, F0, attenuation) * shadow;
}

vec3 XRENGINE_CalcForwardPlusSpotLight(ForwardPlusLocalLight light, vec3 normal, vec3 fragPos, vec3 albedo, vec3 rms, vec3 F0)
{
    vec3 lightDir = normalize(light.DirectionWS_Exponent.xyz);
    vec3 lightVector = light.PositionWS.xyz - fragPos;
    vec3 lightToPosN = normalize(lightVector);
    float clampedCosine = max(0.0, dot(-lightToPosN, lightDir));
    float spotEffect = smoothstep(light.SpotAngles.y, light.SpotAngles.x, clampedCosine);

    float spotAttn = pow(clampedCosine, light.DirectionWS_Exponent.w);
    float distAttn = XRENGINE_Attenuate(length(lightVector), light.Params.x) * max(light.Params.y, 0.0001);
    int sourceIndex = int(light.Params.w + 0.5);
    float shadow = (sourceIndex >= 0 && sourceIndex < SpotLightCount)
        ? XRENGINE_ReadShadowMapSpot(sourceIndex, SpotLights[sourceIndex], normal, fragPos, lightToPosN)
        : 1.0;

    return spotEffect * spotAttn * XRENGINE_CalcForwardPlusColor(light.Color_Type.xyz, light.Params.z, lightToPosN, normal, fragPos, albedo, rms, F0, distAttn) * shadow;
}

// Main lighting calculation function
// Call this from your fragment shader main() with your surface parameters
vec3 XRENGINE_CalculateForwardLighting(vec3 normal, vec3 fragPos, vec3 albedo, float specularIntensity, float ambientOcclusion)
{
    XRENGINE_ForwardShadowDebugModeActive = 0;
    XRENGINE_ForwardShadowDebugColor = vec3(0.0);
    normal = normalize(normal);
    vec3 viewDir = normalize(XRENGINE_GetForwardCameraPosition() - fragPos);
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
        int tileCountX = ForwardPlusTileCountX;
        int tileCountY = ForwardPlusTileCountY;
        ivec2 tileCoord = ivec2(floor(gl_FragCoord.xy - ScreenOrigin)) / ForwardPlusTileSize;
        tileCoord = clamp(tileCoord, ivec2(0), ivec2(tileCountX - 1, tileCountY - 1));
        int tileIndex = XRENGINE_GetForwardViewIndex() * (tileCountX * tileCountY) + tileCoord.y * tileCountX + tileCoord.x;
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
            totalLight += XRENGINE_CalcPointLight(i, PointLights[i], normal, fragPos, albedo, rms, F0);

        for (int i = 0; i < SpotLightCount; ++i)
            totalLight += XRENGINE_CalcSpotLight(i, SpotLights[i], normal, fragPos, albedo, rms, F0);
    }

    if (XRENGINE_ForwardShadowDebugModeActive != 0)
        return XRENGINE_ForwardShadowDebugColor;

    return totalLight + XRENGINE_CalculateAmbientPbr(normal, fragPos, albedo, viewDir, rms, ambientOcclusion) + albedo * Emission;
}

#undef ShadowBlockerSamples
#undef ShadowFilterSamples
#undef ShadowVogelTapCount
#undef SoftShadowMode
#undef EnableCascadedShadows
#undef EnableContactShadows
#undef ContactShadowSamples
#undef ShadowBase
#undef ShadowMult
#undef ShadowBiasMin
#undef ShadowBiasMax
#undef ShadowFilterRadius
#undef ShadowBlockerSearchRadius
#undef ShadowMinPenumbra
#undef ShadowMaxPenumbra
#undef LightSourceRadius
#undef ContactShadowDistance
#undef ContactShadowThickness
#undef ContactShadowFadeStart
#undef ContactShadowFadeEnd
#undef ContactShadowNormalOffset
#undef ContactShadowJitterStrength
