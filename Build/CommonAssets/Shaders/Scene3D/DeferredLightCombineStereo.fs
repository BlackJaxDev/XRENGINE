#version 460
#extension GL_OVR_multiview2 : require
//#extension GL_EXT_multiview_tessellation_geometry_shader : enable

#pragma snippet "NormalEncoding"
#pragma snippet "OctahedralMapping"
#pragma snippet "PBRFunctions"
#pragma snippet "DepthUtils"

// Reflection mip is derived from the texture's mip count; no fixed cap.
const float MAX_REFLECTION_LOD = 4.0f; // Deprecated; retained to avoid breaking includes, actual max is queried per texture.

layout(location = 0) out vec4 OutLo; //Diffuse Light Color, to start off the HDR Scene Texture
layout(location = 0) in vec3 FragPos;

layout(binding = 0) uniform sampler2DArray AlbedoOpacity;
layout(binding = 1) uniform sampler2DArray Normal;
layout(binding = 2) uniform sampler2DArray RMSE;
layout(binding = 3) uniform sampler2DArray AmbientOcclusionTexture;
layout(binding = 4) uniform sampler2DArray DepthView;
layout(binding = 5) uniform sampler2DArray LightingAccumTexture;
layout(binding = 6) uniform sampler2D BRDF;
layout(binding = 7) uniform sampler2DArray IrradianceArray;
layout(binding = 8) uniform sampler2DArray PrefilterArray;

layout(std430, binding = 20) buffer LightProbePositions
{
    vec4 ProbePositions[];
};

layout(std430, binding = 21) buffer LightProbeTetrahedra
{
    vec4 TetraIndices[];
};

struct ProbeParam
{
    vec4 InfluenceInner;       // xyz inner extents or inner radius (x) for sphere
    vec4 InfluenceOuter;       // xyz outer extents or outer radius (x) for sphere
    vec4 InfluenceOffsetShape; // xyz offset, w = shape (0 sphere, 1 box)
    vec4 ProxyCenterEnable;    // xyz center offset, w = parallax enabled (1/0)
    vec4 ProxyHalfExtents;     // xyz half extents, w = normalization scale
    vec4 ProxyRotation;        // xyzw quaternion
};

layout(std430, binding = 22) buffer LightProbeParameters
{
    ProbeParam ProbeParams[];
};

// Sparse grid accelerator: flattened cells with offsets into a compact probe list
struct ProbeGridCell
{
    ivec4 OffsetCount;      // x=offset, y=count
    ivec4 FallbackIndices;  // up to four nearest probes for empty/stale cells
};

layout(std430, binding = 23) buffer LightProbeGridCells
{
    ProbeGridCell GridCells[];
};

layout(std430, binding = 24) buffer LightProbeGridIndices
{
    int CellTetraIndices[];
};

uniform int ProbeCount;
uniform int TetraCount;
uniform ivec3 ProbeGridDims;
uniform vec3 ProbeGridOrigin;
uniform float ProbeGridCellSize;
uniform bool UseProbeGrid;
uniform bool UseAmbientOcclusion = true;
uniform float AmbientOcclusionPower = 1.0f;
uniform bool AmbientOcclusionMultiBounce = false;
uniform bool SpecularOcclusionEnabled = false;
uniform vec3 GlobalAmbient = vec3(0.03f);

// Debug: set via XRE_DEFERRED_DEBUG env var.
// 0 = normal, 1 = raw albedo, 2 = InLo, 3 = RMSE, 4 = normal, 5 = depth,
// 6-9/11-14 = directional debug forwarded from the light shader, 10 = AO.
uniform int DeferredDebugMode = 0;

uniform mat4 LeftEyeInverseViewMatrix;
uniform mat4 RightEyeInverseViewMatrix;
uniform mat4 LeftEyeInverseProjMatrix;
uniform mat4 RightEyeInverseProjMatrix;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform vec2 ScreenOrigin;

// Multi-bounce AO approximation - Jimenez et al. 2016 Section 5.
vec3 MultiBounceAO(float ao, vec3 albedo)
{
    vec3 a = 2.0404f * albedo - 0.3324f;
    vec3 b = -4.7951f * albedo + 0.6417f;
    vec3 c = 2.7552f * albedo + 0.6903f;
    return max(vec3(ao), ((a * ao + b) * ao + c) * ao);
}

// Ground Truth Specular Occlusion - Jimenez et al. 2016 Section 6.
float GTSpecularOcclusion(float NoV, float ao, float roughness)
{
    return clamp(pow(NoV + ao, exp2(-16.0f * roughness - 1.0f)) - 1.0f + ao, 0.0f, 1.0f);
}

float ResolveAmbientOcclusion(vec3 uvi)
{
    if (!UseAmbientOcclusion)
        return 1.0f;

    float rawAO = texture(AmbientOcclusionTexture, uvi).r;
    if (isnan(rawAO) || isinf(rawAO))
        return 1.0f;

    return pow(clamp(rawAO, 0.0f, 1.0f), max(AmbientOcclusionPower, 0.001f));
}

mat3 QuaternionToMat3(vec4 q)
{
    vec3 q2 = q.xyz + q.xyz;
    vec3 qq = q.xyz * q2;
    float qx2 = q.x * q2.x;
    float qy2 = q.y * q2.y;
    float qz2 = q.z * q2.z;

    vec3 qwq = q.w * q2;
    vec3 m0 = vec3(1.0f - (qy2 + qz2), qq.x + qwq.z, qq.y - qwq.y);
    vec3 m1 = vec3(qq.x - qwq.z, 1.0f - (qx2 + qz2), qq.z + qwq.x);
    vec3 m2 = vec3(qq.y + qwq.y, qq.z - qwq.x, 1.0f - (qx2 + qy2));
    return mat3(m0, m1, m2);
}

float ComputeInfluenceWeight(int probeIndex, vec3 worldPos)
{
    ProbeParam p = ProbeParams[probeIndex];
    int shape = int(p.InfluenceOffsetShape.w + 0.5f);
    vec3 center = ProbePositions[probeIndex].xyz + p.InfluenceOffsetShape.xyz;
    if (shape == 0)
    {
        float inner = p.InfluenceInner.w;
        float outer = max(p.InfluenceOuter.w, inner + 0.0001f);
        float dist = length(worldPos - center);
        float ndf = clamp((dist - inner) / (outer - inner), 0.0f, 1.0f);
        return 1.0f - ndf;
    }

    vec3 inner = p.InfluenceInner.xyz;
    vec3 outer = max(p.InfluenceOuter.xyz, inner + vec3(0.0001f));
    vec3 rel = abs(worldPos - center);
    vec3 ndf3 = clamp((rel - inner) / (outer - inner), 0.0f, 1.0f);
    float ndf = max(ndf3.x, max(ndf3.y, ndf3.z));
    return 1.0f - ndf;
}

vec3 ApplyParallax(int probeIndex, vec3 dirWS, vec3 worldPos)
{
    ProbeParam p = ProbeParams[probeIndex];
    if (p.ProxyCenterEnable.w < 0.5f)
        return dirWS;

    vec3 proxyCenter = ProbePositions[probeIndex].xyz + p.ProxyCenterEnable.xyz;
    vec3 halfExt = max(p.ProxyHalfExtents.xyz, vec3(0.0001f));
    mat3 rot = QuaternionToMat3(p.ProxyRotation);
    mat3 invRot = transpose(rot);

    vec3 rayOrigLS = invRot * (worldPos - proxyCenter);
    vec3 rayDirLS = normalize(invRot * dirWS);
    vec3 t1 = (halfExt - rayOrigLS) / max(rayDirLS, vec3(1e-6f));
    vec3 t2 = (-halfExt - rayOrigLS) / max(rayDirLS, vec3(1e-6f));
    vec3 tmax = max(t1, t2);
    float dist = min(tmax.x, min(tmax.y, tmax.z));
    if (dist <= 0.0f || isnan(dist) || isinf(dist))
        return dirWS;

    vec3 hitLS = rayOrigLS + rayDirLS * dist;
    vec3 hitWS = proxyCenter + rot * hitLS;
    return normalize(hitWS - ProbePositions[probeIndex].xyz);
}

bool ComputeBarycentric(vec3 p, vec3 a, vec3 b, vec3 c, vec3 d, out vec4 bary)
{
    mat3 m = mat3(b - a, c - a, d - a);
    vec3 v = p - a;
    vec3 uvw = inverse(m) * v;
    float w = 1.0f - uvw.x - uvw.y - uvw.z;
    bary = vec4(w, uvw);
    return bary.x >= -0.0001f && bary.y >= -0.0001f && bary.z >= -0.0001f && bary.w >= -0.0001f;
}

#ifdef XRENGINE_PROBE_DEBUG_FALLBACK
void ResolveProbeWeights(vec3 worldPos, out vec4 weights, out ivec4 indices)
{
    weights = vec4(0.0f);
    indices = ivec4(-1);

    for (int i = 0; i < TetraCount; ++i)
    {
        ivec4 idx = ivec4(TetraIndices[i]);
        if (idx.x < 0 || idx.w < 0 || idx.x >= ProbeCount || idx.y >= ProbeCount || idx.z >= ProbeCount || idx.w >= ProbeCount)
            continue;

        vec4 bary;
        vec3 a = ProbePositions[idx.x].xyz;
        vec3 b = ProbePositions[idx.y].xyz;
        vec3 c = ProbePositions[idx.z].xyz;
        vec3 d = ProbePositions[idx.w].xyz;

        if (ComputeBarycentric(worldPos, a, b, c, d, bary))
        {
            weights = bary;
            indices = idx;
            return;
        }
    }

    float bestDistances[4];
    int bestIndices[4];
    for (int k = 0; k < 4; ++k)
    {
        bestDistances[k] = 1e20f;
        bestIndices[k] = -1;
    }

    for (int i = 0; i < ProbeCount; ++i)
    {
        float d = length(worldPos - ProbePositions[i].xyz);
        for (int k = 0; k < 4; ++k)
        {
            if (d < bestDistances[k])
            {
                for (int s = 3; s > k; --s)
                {
                    bestDistances[s] = bestDistances[s - 1];
                    bestIndices[s] = bestIndices[s - 1];
                }
                bestDistances[k] = d;
                bestIndices[k] = i;
                break;
            }
        }
    }

    float sum = 0.0f;
    for (int k = 0; k < 4; ++k)
    {
        if (bestIndices[k] >= 0)
        {
            float w = 1.0f / max(bestDistances[k], 0.0001f);
            weights[k] = w;
            indices[k] = bestIndices[k];
            sum += w;
        }
    }
    if (sum > 0.0f)
        weights /= sum;
}
#endif

void ResolveProbeWeightsGrid(vec3 worldPos, out vec4 weights, out ivec4 indices, out bool isBarycentric)
{
    weights = vec4(0.0f);
    indices = ivec4(-1);
    isBarycentric = false;

    if (ProbeGridDims.x <= 0 || ProbeGridDims.y <= 0 || ProbeGridDims.z <= 0 || ProbeGridCellSize <= 0.0f)
        return;

    vec3 rel = (worldPos - ProbeGridOrigin) / ProbeGridCellSize;
    ivec3 cell = clamp(ivec3(floor(rel)), ivec3(0), ProbeGridDims - ivec3(1));
    int flatIndex = cell.x + cell.y * ProbeGridDims.x + cell.z * ProbeGridDims.x * ProbeGridDims.y;

    ProbeGridCell cellData = GridCells[flatIndex];
    ivec2 oc = cellData.OffsetCount.xy;
    int offset = oc.x;
    int count = oc.y;
    if (count > 0)
    {
        for (int i = 0; i < count; ++i)
        {
            int tetraIndex = CellTetraIndices[offset + i];
            if (tetraIndex < 0 || tetraIndex >= TetraCount)
                continue;

            ivec4 idx = ivec4(TetraIndices[tetraIndex]);
            if (idx.x < 0 || idx.w < 0 || idx.x >= ProbeCount || idx.y >= ProbeCount || idx.z >= ProbeCount || idx.w >= ProbeCount)
                continue;

            vec4 bary;
            vec3 a = ProbePositions[idx.x].xyz;
            vec3 b = ProbePositions[idx.y].xyz;
            vec3 c = ProbePositions[idx.z].xyz;
            vec3 d = ProbePositions[idx.w].xyz;
            if (ComputeBarycentric(worldPos, a, b, c, d, bary))
            {
                weights = bary;
                indices = idx;
                isBarycentric = true;
                return;
            }
        }
    }

    float sum = 0.0f;
    for (int k = 0; k < 4; ++k)
    {
        int probeIndex = cellData.FallbackIndices[k];
        if (probeIndex < 0 || probeIndex >= ProbeCount)
            continue;

        float d = length(worldPos - ProbePositions[probeIndex].xyz);
        float w = 1.0f / max(d, 0.0001f);
        weights[k] = w;
        indices[k] = probeIndex;
        sum += w;
    }

    if (sum > 0.0f)
        weights /= sum;
}

void main()
{
    vec2 uv = clamp(
        XRENGINE_FramebufferUV(gl_FragCoord.xy, ScreenOrigin, vec2(ScreenWidth, ScreenHeight)),
        vec2(0.0f),
        vec2(1.0f));

    float layer = float(gl_ViewID_OVR);
    vec3 uvi = vec3(uv, layer);
    bool leftEye = gl_ViewID_OVR == 0;
    mat4 inverseViewMatrix = leftEye ? LeftEyeInverseViewMatrix : RightEyeInverseViewMatrix;
    mat4 inverseProjMatrix = leftEye ? LeftEyeInverseProjMatrix : RightEyeInverseProjMatrix;

    vec4 albedoOpacity = texture(AlbedoOpacity, uvi);
    vec3 albedoColor = albedoOpacity.rgb;
    vec3 normal = XRENGINE_ReadNormal(Normal, uvi);
    vec4 rmse = texture(RMSE, uvi);
    float depth = texture(DepthView, uvi).r;
    vec3 inLo = max(texture(LightingAccumTexture, uvi).rgb, vec3(0.0f));

    // Show raw depth before the far-depth discard so XRE_DEFERRED_DEBUG=5 can
    // distinguish an empty G-buffer from valid geometry with no lighting.
    if (DeferredDebugMode == 5) { OutLo = vec4(vec3(depth), 1.0f); return; }
    if (XRENGINE_ResolveDepth(depth) >= 1.0f)
    {
        OutLo = vec4(0.0f);
        return;
    }

    float ao = ResolveAmbientOcclusion(uvi);

    if (DeferredDebugMode > 0)
    {
        if (DeferredDebugMode == 1) { OutLo = vec4(albedoColor, 1.0f); return; }
        if (DeferredDebugMode == 2) { OutLo = vec4(inLo, 1.0f); return; }
        if (DeferredDebugMode == 3) { OutLo = vec4(rmse.rgb, 1.0f); return; }
        if (DeferredDebugMode == 4) { OutLo = vec4(normal * 0.5f + 0.5f, 1.0f); return; }
        if ((DeferredDebugMode >= 6 && DeferredDebugMode <= 9) || (DeferredDebugMode >= 11 && DeferredDebugMode <= 18)) { OutLo = vec4(inLo, 1.0f); return; }
        if (DeferredDebugMode == 10) { OutLo = vec4(vec3(ao), 1.0f); return; }
    }

    vec3 fragPosWS = XRENGINE_WorldPosFromDepthRaw(depth, uv, inverseProjMatrix, inverseViewMatrix);

    float roughness = rmse.x;
    float metallic = rmse.y;
    float specularIntensity = rmse.z;
    float emissiveIntensity = rmse.w;

    vec3 cameraPosition = inverseViewMatrix[3].xyz;
    vec3 V = normalize(cameraPosition - fragPosWS);
    float NoV = max(dot(normal, V), 0.0f);
    vec3 F0 = mix(vec3(0.04f), albedoColor, metallic);
    vec2 brdfValue = texture(BRDF, vec2(NoV, roughness)).rg;

    vec3 kS = XRENGINE_F_SchlickRoughnessFast(NoV, F0, roughness) * specularIntensity;
    vec3 kD = (1.0f - kS) * (1.0f - metallic);
    vec3 R = reflect(-V, normal);

    vec4 probeWeights = vec4(0.0f);
    ivec4 probeIndices = ivec4(-1);
    bool isBarycentric = false;
    if (UseProbeGrid)
        ResolveProbeWeightsGrid(fragPosWS, probeWeights, probeIndices, isBarycentric);
#ifdef XRENGINE_PROBE_DEBUG_FALLBACK
    else
        ResolveProbeWeights(fragPosWS, probeWeights, probeIndices);
#endif

    vec3 probeAmbient = vec3(1.0f);
    vec3 irradianceColor = vec3(0.0f);
    vec3 prefilteredColor = vec3(0.0f);
    float totalWeight = 0.0f;

    for (int i = 0; i < 4; ++i)
    {
        if (probeIndices[i] < 0)
            continue;

        float w = isBarycentric
            ? probeWeights[i]
            : probeWeights[i] * ComputeInfluenceWeight(probeIndices[i], fragPosWS);
        if (w <= 0.0f)
            continue;

        vec3 parallaxDir = ApplyParallax(probeIndices[i], normal, fragPosWS);
        vec3 reflDir = ApplyParallax(probeIndices[i], R, fragPosWS);
        float normScale = max(ProbeParams[probeIndices[i]].ProxyHalfExtents.w, 0.0001f);
        float maxMip = float(textureQueryLevels(PrefilterArray) - 1);
        float clampedLod = min(roughness * MAX_REFLECTION_LOD, maxMip);

        irradianceColor += w * normScale * XRENGINE_SampleOctaArray(IrradianceArray, parallaxDir, probeIndices[i]);
        prefilteredColor += w * normScale * XRENGINE_SampleOctaArrayLod(PrefilterArray, reflDir, probeIndices[i], clampedLod);
        totalWeight += w;
    }

    if (totalWeight > 0.0f)
    {
        irradianceColor /= totalWeight;
        prefilteredColor /= totalWeight;
        probeAmbient = irradianceColor;
    }
    else if (ProbeCount > 0)
    {
        float maxMip = float(textureQueryLevels(PrefilterArray) - 1);
        float clampedLod = min(roughness * MAX_REFLECTION_LOD, maxMip);
        irradianceColor = XRENGINE_SampleOctaArray(IrradianceArray, normal, 0.0f);
        prefilteredColor = XRENGINE_SampleOctaArrayLod(PrefilterArray, normal, 0.0f, clampedLod);
        probeAmbient = irradianceColor;
    }

    vec3 diffuse = GlobalAmbient * probeAmbient * albedoColor;
    vec3 specular = prefilteredColor * (kS * brdfValue.x + brdfValue.y);

    vec3 diffuseAO = AmbientOcclusionMultiBounce ? MultiBounceAO(ao, albedoColor) : vec3(ao);
    float specOcclusion = SpecularOcclusionEnabled ? GTSpecularOcclusion(NoV, ao, roughness) : ao;
    vec3 ambient = kD * diffuse * diffuseAO + specular * specOcclusion;

    OutLo = vec4(ambient + inLo + emissiveIntensity * albedoColor, albedoOpacity.a);
}
