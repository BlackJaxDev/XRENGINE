// Exact admitted forward probe-grid/IBL arithmetic from Snippets/ForwardLighting.glsl.
// Raw word buffers preserve the shared engine producer layouts and numeric bindings20..24.
struct UberPbrUniforms
{
    int ProbeCount; int TetraCount; uint ForwardPbrResourcesEnabled; uint UseProbeGrid;
    float3 ProbeGridOrigin; float ProbeGridCellSize;
    int3 ProbeGridDims; uint SpecularOcclusionEnabled;
};
struct ProbeParam
{
    float4 InfluenceInner; float4 InfluenceOuter; float4 InfluenceOffsetShape;
    float4 ProxyCenterEnable; float4 ProxyHalfExtents; float4 ProxyRotation;
};
struct ProbeGridCell { int4 OffsetCount; int4 FallbackIndices; };
[[vk::binding(3, 2)]] ConstantBuffer<UberPbrUniforms> uberPbr;
[[vk::binding(4, 2)]] Texture2D<float4> BRDF;
[[vk::binding(5, 2)]] SamplerState BRDFSampler;
[[vk::binding(6, 2)]] Texture2DArray<float4> IrradianceArray;
[[vk::binding(7, 2)]] SamplerState IrradianceArraySampler;
[[vk::binding(8, 2)]] Texture2DArray<float4> PrefilterArray;
[[vk::binding(9, 2)]] SamplerState PrefilterArraySampler;
[[vk::binding(20, 2)]] StructuredBuffer<uint> uberPositions;
[[vk::binding(21, 2)]] StructuredBuffer<uint> uberTetrahedra;
[[vk::binding(22, 2)]] StructuredBuffer<uint> uberProbeParameters;
[[vk::binding(23, 2)]] StructuredBuffer<uint> uberGridCells;
[[vk::binding(24, 2)]] StructuredBuffer<uint> uberGridIndices;
float4 uberPbrPosition(int index) { uint w=uint(index)*4u; return asfloat(uint4(uberPositions[w],uberPositions[w+1u],uberPositions[w+2u],uberPositions[w+3u])); }
float4 uberPbrTetra(int index) { uint w=uint(index)*4u; return asfloat(uint4(uberTetrahedra[w],uberTetrahedra[w+1u],uberTetrahedra[w+2u],uberTetrahedra[w+3u])); }
float4 uberPbrParameterVector(uint w) { return asfloat(uint4(uberProbeParameters[w],uberProbeParameters[w+1u],uberProbeParameters[w+2u],uberProbeParameters[w+3u])); }
ProbeParam uberPbrParameters(int index)
{
    uint w=uint(index)*24u; ProbeParam p;
    p.InfluenceInner=uberPbrParameterVector(w); p.InfluenceOuter=uberPbrParameterVector(w+4u);
    p.InfluenceOffsetShape=uberPbrParameterVector(w+8u); p.ProxyCenterEnable=uberPbrParameterVector(w+12u);
    p.ProxyHalfExtents=uberPbrParameterVector(w+16u); p.ProxyRotation=uberPbrParameterVector(w+20u); return p;
}
ProbeGridCell uberPbrCell(int index)
{
    uint w=uint(index)*8u; ProbeGridCell c;
    c.OffsetCount=int4(uberGridCells[w],uberGridCells[w+1u],uberGridCells[w+2u],uberGridCells[w+3u]);
    c.FallbackIndices=int4(uberGridCells[w+4u],uberGridCells[w+5u],uberGridCells[w+6u],uberGridCells[w+7u]); return c;
}
float2 uberPbrOctaUv(float3 dir)
{
    float3 n=dir.xzy; n/=max(abs(n.x)+abs(n.y)+abs(n.z),1e-5); float2 uv=n.xy;
    if(n.z<0.0) uv=(1.0-abs(uv.yx))*float2(n.x>=0.0?1.0:-1.0,n.y>=0.0?1.0:-1.0);
    return uv*0.5+0.5;
}
float3 XRENGINE_MultiBounceAO(float ao,float3 albedo)
{
    float3 a=2.0404*albedo-0.3324,b=-4.7951*albedo+0.6417,c=2.7552*albedo+0.6903;
    return max(float3(ao),((a*ao+b)*ao+c)*ao);
}
float XRENGINE_GTSpecularOcclusion(float NoV,float ao,float roughness)
{ return clamp(pow(NoV+ao,exp2(-16.0*roughness-1.0))-1.0+ao,0.0,1.0); }
float3 XRENGINE_SpecF_SchlickRoughnessApprox(float VoH,float3 F0,float roughness)
{ return F0+(max(float3(1.0-roughness),F0)-F0)*exp2((-5.55473*VoH-6.98316)*VoH); }

float3x3 XRENGINE_QuaternionToMat3(float4 q)
{
    float3 q2 = q.xyz + q.xyz;
    float3 qq = q.xyz * q2;
    float qx2 = q.x * q2.x;
    float qy2 = q.y * q2.y;
    float qz2 = q.z * q2.z;

    float3 qwq = q.w * q2;
    float3 m0 = float3(1.0 - (qy2 + qz2), qq.x + qwq.z, qq.y - qwq.y);
    float3 m1 = float3(qq.x - qwq.z, 1.0 - (qx2 + qz2), qq.z + qwq.x);
    float3 m2 = float3(qq.y + qwq.y, qq.z - qwq.x, 1.0 - (qx2 + qy2));
    return transpose(float3x3(m0, m1, m2));
}

float XRENGINE_ComputeInfluenceWeight(int probeIndex, float3 worldPos)
{
    ProbeParam p = uberPbrParameters(probeIndex);
    int shape = int(p.InfluenceOffsetShape.w + 0.5);
    float3 center = uberPbrPosition(probeIndex).xyz + p.InfluenceOffsetShape.xyz;
    if (shape == 0)
    {
        float inner = p.InfluenceInner.w;
        float outer = max(p.InfluenceOuter.w, inner + 0.0001);
        float dist = length(worldPos - center);
        return smoothstep(outer, inner, dist);
    }

    float3 inner = p.InfluenceInner.xyz;
    float3 outer = max(p.InfluenceOuter.xyz, inner + float3(0.0001));
    float3 rel = abs(worldPos - center);
    float3 ndf3 = clamp((rel - inner) / (outer - inner), 0.0, 1.0);
    float ndf = max(ndf3.x, max(ndf3.y, ndf3.z));
    return smoothstep(1.0, 0.0, ndf);
}

float3 XRENGINE_ApplyParallax(int probeIndex, float3 dirWS, float3 worldPos)
{
    ProbeParam p = uberPbrParameters(probeIndex);
    if (p.ProxyCenterEnable.w < 0.5)
        return dirWS;

    float3 proxyCenter = uberPbrPosition(probeIndex).xyz + p.ProxyCenterEnable.xyz;
    float3 halfExt = max(p.ProxyHalfExtents.xyz, float3(0.0001));
    float3x3 rot = XRENGINE_QuaternionToMat3(p.ProxyRotation);
    float3x3 invRot = transpose(rot);

    float3 rayOrigLS = mul(invRot, (worldPos - proxyCenter));
    float3 rayDirLS = normalize(mul(invRot, dirWS));

    // Avoid division by zero per-component without a long branch chain.
    float3 absDir = abs(rayDirLS);
    float3 signDir = lerp(float3(1.0), sign(rayDirLS), step(float3(1e-6), absDir));
    float3 safeDir = signDir * max(absDir, float3(1e-6));
    float3 t1 = (halfExt - rayOrigLS) / safeDir;
    float3 t2 = (-halfExt - rayOrigLS) / safeDir;
    float3 tmax = max(t1, t2);
    float dist = min(tmax.x, min(tmax.y, tmax.z));
    if (dist <= 0.0 || isnan(dist) || isinf(dist))
        return dirWS;

    float3 hitLS = rayOrigLS + rayDirLS * dist;
    float3 hitWS = proxyCenter + mul(rot, hitLS);
    return normalize(hitWS - uberPbrPosition(probeIndex).xyz);
}

bool XRENGINE_ComputeBarycentric(float3 p, float3 a, float3 b, float3 c, float3 d, out float4 bary)
{
    // Solve for (u, v, w) in m * (u v w)^T = (p - a) using Cramer's rule.
    // This avoids the full float3x3 inverse (cofactor expansion) and is several
    // times cheaper. m's columns are (b-a, c-a, d-a).
    float3 e1 = b - a;
    float3 e2 = c - a;
    float3 e3 = d - a;
    float3 v = p - a;
    float3 e2xe3 = cross(e2, e3);
    float det = dot(e1, e2xe3);
    float invDet = 1.0 / (abs(det) > 1e-20 ? det : 1e-20);
    float u = dot(v, e2xe3) * invDet;
    float vCoord = dot(e1, cross(v, e3)) * invDet;
    float wCoord = dot(e1, cross(e2, v)) * invDet;
    float w0 = 1.0 - u - vCoord - wCoord;
    bary = float4(w0, u, vCoord, wCoord);
    return bary.x >= -0.0001 && bary.y >= -0.0001 && bary.z >= -0.0001 && bary.w >= -0.0001;
}


void XRENGINE_ResolveProbeWeightsGrid(float3 worldPos, out float4 weights, out int4 indices, out bool isBarycentric)
{
    weights = float4(0.0);
    indices = int4(-1);
    isBarycentric = false;

    if (uberPbr.ProbeGridDims.x <= 0 || uberPbr.ProbeGridDims.y <= 0 || uberPbr.ProbeGridDims.z <= 0 || uberPbr.ProbeGridCellSize <= 0.0)
        return;

    float3 rel = (worldPos - uberPbr.ProbeGridOrigin) / uberPbr.ProbeGridCellSize;
    int3 cell = clamp(int3(floor(rel)), int3(0), uberPbr.ProbeGridDims - int3(1));
    int flatIndex = cell.x + cell.y * uberPbr.ProbeGridDims.x + cell.z * uberPbr.ProbeGridDims.x * uberPbr.ProbeGridDims.y;
    ProbeGridCell cellData = uberPbrCell(flatIndex);
    int2 offsetCount = cellData.OffsetCount.xy;

    if (offsetCount.y > 0)
    {
        for (int i = 0; i < offsetCount.y; ++i)
        {
            int tetraIndex = int(uberGridIndices[offsetCount.x + i]);
            if (tetraIndex < 0 || tetraIndex >= uberPbr.TetraCount)
                continue;

            int4 idx = int4(uberPbrTetra(tetraIndex));
            if (idx.x < 0 || idx.w < 0 || idx.x >= uberPbr.ProbeCount || idx.y >= uberPbr.ProbeCount || idx.z >= uberPbr.ProbeCount || idx.w >= uberPbr.ProbeCount)
                continue;

            float4 bary;
            if (XRENGINE_ComputeBarycentric(worldPos, uberPbrPosition(idx.x).xyz, uberPbrPosition(idx.y).xyz, uberPbrPosition(idx.z).xyz, uberPbrPosition(idx.w).xyz, bary))
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
        if (probeIndex < 0 || probeIndex >= uberPbr.ProbeCount)
            continue;

        float dist = length(worldPos - uberPbrPosition(probeIndex).xyz);
        float weight = 1.0 / max(dist, 0.0001);
        weights[k] = weight;
        indices[k] = probeIndex;
        sum += weight;
    }

    if (sum > 0.0)
        weights /= sum;
}
float3 XRENGINE_ResolveForwardAmbientFallback()
{
    float ambientMax = max(lighting.globalAmbient.xyz.r, max(lighting.globalAmbient.xyz.g, lighting.globalAmbient.xyz.b));
    if (ambientMax <= 0.0001)
        return float3(0.08);

    float scale = max(1.0, 0.08 / ambientMax);
    return lighting.globalAmbient.xyz * scale;
}

float3 XRENGINE_GetForwardAmbientFallback(float3 albedo, float3 diffuseAO)
{
    return XRENGINE_ResolveForwardAmbientFallback() * albedo * diffuseAO;
}

float3 XRENGINE_CalculateAmbientPbr(float3 normal, float3 fragPos, float3 albedo, float3 viewDir, float3 rms, float ambientOcclusion)
{
    float roughness = rms.x;
    float metallic = rms.y;
    float specularIntensity = rms.z;

    float NoV = max(dot(normal, viewDir), 0.0);
    float3 F0 = lerp(float3(0.04), albedo, metallic);
    float3 kS = XRENGINE_SpecF_SchlickRoughnessApprox(NoV, F0, roughness) * specularIntensity;
    float3 kD = (1.0 - kS) * (1.0 - metallic);
    float3 diffuseAO = (lighting.ambientOcclusionControls.z > 0.5)
        ? XRENGINE_MultiBounceAO(ambientOcclusion, albedo)
        : float3(ambientOcclusion);

#ifdef XRE_UBER_BASE_DISABLE_PBR_RESOURCES
    return XRENGINE_GetForwardAmbientFallback(albedo, diffuseAO);
#else
    float specularOcclusion = (uberPbr.SpecularOcclusionEnabled != 0u)
        ? XRENGINE_GTSpecularOcclusion(NoV, ambientOcclusion, roughness)
        : ambientOcclusion;

    if (!(uberPbr.ForwardPbrResourcesEnabled != 0u) || uberPbr.ProbeCount <= 0)
        return XRENGINE_GetForwardAmbientFallback(albedo, diffuseAO);

    float4 probeWeights = float4(0.0);
    int4 probeIndices = int4(-1);
    bool isBarycentric = false;
    if ((uberPbr.UseProbeGrid != 0u))
        XRENGINE_ResolveProbeWeightsGrid(fragPos, probeWeights, probeIndices, isBarycentric);
#ifdef XRENGINE_PROBE_DEBUG_FALLBACK
    else
        XRENGINE_ResolveProbeWeights(fragPos, probeWeights, probeIndices);
#endif

    float3 reflectionDir = reflect(-viewDir, normal);
    float3 irradianceColor = float3(0.0);
    float3 prefilteredColor = float3(0.0);
    float totalWeight = 0.0;
    // Loop-invariant prefilter LOD bounds: hoisted out of the per-probe loop.
    uint prefilterWidth, prefilterHeight, prefilterLayers, prefilterLevels;
    PrefilterArray.GetDimensions(0u, prefilterWidth, prefilterHeight, prefilterLayers, prefilterLevels);
    float maxMip = float(prefilterLevels - 1u);
    float clampedLod = min(roughness * 4.0, maxMip);

    for (int i = 0; i < 4; ++i)
    {
        int probeIndex = probeIndices[i];
        if (probeIndex < 0)
            continue;

        // Barycentric tetra weights are already mathematically continuous.
        // Multiplying by per-probe influence would create hard edges at
        // influence-sphere boundaries, so we only apply influence for the
        // distance-weighted fallback path.
        float weight = isBarycentric
            ? probeWeights[i]
            : probeWeights[i] * XRENGINE_ComputeInfluenceWeight(probeIndex, fragPos);
        if (weight <= 0.0)
            continue;

        float3 diffuseDir = XRENGINE_ApplyParallax(probeIndex, normal, fragPos);
        float3 specDir = XRENGINE_ApplyParallax(probeIndex, reflectionDir, fragPos);
        float normalizationScale = max(uberPbrParameters(probeIndex).ProxyHalfExtents.w, 0.0001);
        float weightedScale = weight * normalizationScale;

        // The admitted canonical array sampler is linear, non-mipmapped and
        // non-anisotropic, so explicit level zero preserves its exact footprint
        // while varying probe weights remain legal WGSL control flow.
        irradianceColor += weightedScale * IrradianceArray.SampleLevel(IrradianceArraySampler, float3(uberPbrOctaUv(diffuseDir), probeIndex), 0.0).rgb;
        prefilteredColor += weightedScale * PrefilterArray.SampleLevel(PrefilterArraySampler, float3(uberPbrOctaUv(specDir), probeIndex), clampedLod).rgb;
        totalWeight += weight;
    }

    if (totalWeight <= 0.0)
    {
        return XRENGINE_GetForwardAmbientFallback(albedo, diffuseAO);
    }

    irradianceColor /= totalWeight;
    prefilteredColor /= totalWeight;

    // The shared BRDF producer owns one linearly sampled mip with anisotropy one.
    float2 brdfValue = BRDF.SampleLevel(BRDFSampler, float2(NoV, roughness), 0.0).rg;
    float3 diffuse = lighting.globalAmbient.xyz * irradianceColor * albedo;
    float3 specular = prefilteredColor * (kS * brdfValue.x + brdfValue.y);
    return kD * diffuse * diffuseAO + specular * specularOcclusion;
#endif
}
