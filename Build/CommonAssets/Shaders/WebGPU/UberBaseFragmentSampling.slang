// Exact implicit fragment sampling shared by generic and native Uber raster lowerings.
// The including pass declares the seven canonical image/sampler names.
float4 uberBaseSampleColor(UberBaseMaterialParameters material, UberBaseMesh mesh, float time)
{
    float2 uv = uberBaseTransformUv(uberBaseUv(material._MainTexUV, mesh),
        material._MainTex_ST, material._MainTexPan, time);
    return uberBaseColor(material, _MainTex.Sample(_MainTexSampler, uv), mesh.vertexColor);
}

float uberBaseSampleAlpha(UberBaseMaterialParameters material, UberBaseMesh mesh, float alpha, uint features, float time)
{
    float4 sample = float4(1.0);
#if (XRE_UBER_BASE_FEATURES & 2) != 0 || defined(XRE_UBER_BASE_ALL_TEXTURES)
    float2 uv = uberBaseTransformUv(uberBaseUv(material._AlphaMaskUV, mesh),
        material._AlphaMask_ST, material._AlphaMaskPan, time);
    if ((features & UberBaseAlphaMasks) != 0u && material._MainAlphaMaskMode > 0)
        sample = _AlphaMask.Sample(_AlphaMaskSampler, uv);
#endif
    return uberBaseAlpha(material, features, alpha, sample);
}

#ifndef XRE_UBER_BASE_DEPTH
float3 uberBaseSampleNormal(UberBaseMaterialParameters material, UberBaseMesh mesh, uint features, float time)
{
#if (XRE_UBER_BASE_FEATURES & 1) != 0 || defined(XRE_UBER_BASE_ALL_TEXTURES)
    if ((features & UberBaseNormalMap) == 0u) return mesh.vertexNormal;
    float3 positionDx = ddx(mesh.worldPosition), positionDy = ddy(mesh.worldPosition);
    float2 uvDx = ddx(mesh.uv0), uvDy = ddy(mesh.uv0);
    if (abs(material._BumpScale) <= 1e-6) return mesh.vertexNormal;
    UberBaseFrame frame = uberBaseImportedOrFallbackFrame(mesh, positionDx, positionDy, uvDx, uvDy);
    float2 uv = uberBaseTransformUv(uberBaseUv(material._BumpMapUV, mesh),
        material._BumpMap_ST, material._BumpMapPan, time);
    float3 normal;
    if (material.NormalMapMode == 1)
    {
        uint width, height;
        _BumpMap.GetDimensions(width, height);
        float2 texel = 1.0 / float2(width, height);
        float tl = _BumpMap.Sample(_BumpMapSampler, uv + float2(-texel.x, -texel.y)).r;
        float t = _BumpMap.Sample(_BumpMapSampler, uv + float2(0.0, -texel.y)).r;
        float tr = _BumpMap.Sample(_BumpMapSampler, uv + float2(texel.x, -texel.y)).r;
        float l = _BumpMap.Sample(_BumpMapSampler, uv + float2(-texel.x, 0.0)).r;
        float r = _BumpMap.Sample(_BumpMapSampler, uv + float2(texel.x, 0.0)).r;
        float bl = _BumpMap.Sample(_BumpMapSampler, uv + float2(-texel.x, texel.y)).r;
        float b = _BumpMap.Sample(_BumpMapSampler, uv + float2(0.0, texel.y)).r;
        float br = _BumpMap.Sample(_BumpMapSampler, uv + float2(texel.x, texel.y)).r;
        float scale = abs(material.HeightMapScale) > 1e-6 ? material.HeightMapScale : 1.0;
        normal = uberBaseSobelNormal(tl, t, tr, l, r, bl, b, br, scale * material._BumpScale);
    }
    else normal = uberBaseUnpackNormal(_BumpMap.Sample(_BumpMapSampler, uv), material._BumpScale);
    return uberBaseMappedNormal(normal, frame);
#else
    return mesh.vertexNormal;
#endif
}

float3 uberBaseSampleRms(UberBaseMaterialParameters material, UberBaseMesh mesh, uint features, float time)
{
    float4 metallic = float4(0.0), smoothness = float4(0.0), specular = float4(1.0);
#if (XRE_UBER_BASE_FEATURES & 4) != 0 || defined(XRE_UBER_BASE_ALL_TEXTURES)
    float2 metallicUv = uberBaseTransformUv(uberBaseUv(material._PBRMetallicMapsUV, mesh),
        material._PBRMetallicMaps_ST, material._PBRMetallicMapsPan, time);
    float2 smoothnessUv = uberBaseTransformUv(uberBaseUv(material._PBRSmoothnessMapsUV, mesh),
        material._PBRSmoothnessMaps_ST, material._PBRSmoothnessMapsPan, time);
    if ((features & UberBaseAdvancedSpecular) != 0u)
    {
    metallic = _PBRMetallicMaps.Sample(_PBRMetallicMapsSampler, metallicUv);
    smoothness = _PBRSmoothnessMaps.Sample(_PBRSmoothnessMapsSampler, smoothnessUv);
    specular = _SpecularMap.Sample(_SpecularMapSampler, mesh.uv0 * material._SpecularMap_ST.xy + material._SpecularMap_ST.zw);
    }
#endif
    return uberBaseRms(material, features, metallic, smoothness, specular);
}

float3 uberBaseSampleEmission(UberBaseMaterialParameters material, UberBaseMesh mesh, uint features, float time)
{
    float4 sample = float4(0.0);
#if (XRE_UBER_BASE_FEATURES & 8) != 0 || defined(XRE_UBER_BASE_ALL_TEXTURES)
    float2 uv = uberBaseEmissionUv(material, mesh, time);
    if ((features & UberBaseEmission) != 0u)
        sample = _EmissionMap.Sample(_EmissionMapSampler, uv);
#endif
    return uberBaseEmissionColor(material, features, sample);
}
#endif
