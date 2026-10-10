// Arithmetic shared by the exact canonical Uber base raster and native
// lowerings. Source semantics are Uber/UberShader.frag and Uber/common.glsl.
// Optional layered, stylized, displacement and post effects are not represented.
#ifndef XRE_UBER_BASE_SURFACE
#define XRE_UBER_BASE_SURFACE

static const uint UberBaseNormalMap = 1u;
static const uint UberBaseAlphaMasks = 2u;
static const uint UberBaseAdvancedSpecular = 4u;
static const uint UberBaseEmission = 8u;
static const uint UberBaseRenderTime = 16u;

struct UberBaseMaterialParameters
{
    float4 _Color;
    float4 _MainTex_ST;
    float2 _MainTexPan;
    int _MainTexUV;
    float _MainVertexColoringEnabled;
    float _MainVertexColoringLinearSpace;
    float _MainVertexColoring;
    float _MainUseVertexColorAlpha;
    float _AlphaMod;
    float4 _AlphaMask_ST;
    float2 _AlphaMaskPan;
    int _AlphaMaskUV;
    int _MainAlphaMaskMode;
    float _AlphaMaskBlendStrength;
    float _AlphaMaskValue;
    float _AlphaMaskInvert;
    float _Cutoff;
    int _Mode;
    float _AlphaForceOpaque;
    float _SpecularSmoothness;
    float _SpecularStrength;
    float4 _BumpMap_ST;
    float2 _BumpMapPan;
    int _BumpMapUV;
    float _BumpScale;
    int NormalMapMode;
    float HeightMapScale;
    float _PBRMetallicMapInvert;
    float _PBRMetallicMultiplier;
    float4 _PBRMetallicMaps_ST;
    float2 _PBRMetallicMapsPan;
    int _PBRMetallicMapsUV;
    int _PBRMetallicMapsMetallicChannel;
    float4 _PBRSmoothnessMaps_ST;
    float2 _PBRSmoothnessMapsPan;
    int _PBRSmoothnessMapsUV;
    int _PBRSmoothnessMapsChannel;
    float _PBRSmoothnessMapInvert;
    float _PBRRoughnessMultiplier;
    int _SpecularType;
    float _StylizedSpecular;
    float4 _SpecularMap_ST;
    float4 _SpecularTint;
    float4 _EmissionMap_ST;
    float2 _EmissionMapPan;
    int _EmissionMapUV;
    float _EmissionStrength;
    float4 _EmissionColor;
    float2 _EmissionScrollingSpeed;
    float _EmissionScrollingEnabled;
    float _EmissionScrollingVertexColor;
};

struct UberBaseMesh
{
    float3 worldPosition;
    float3 localPosition;
    float3 vertexNormal;
    float3 worldNormal;
    float3 viewDirection;
    float3 tangent;
    float3 bitangent;
    float4 vertexColor;
    float2 uv0;
    float2 uv1;
    float2 uv2;
    float2 uv3;
};

struct UberBaseFrame
{
    float3 tangent;
    float3 bitangent;
    float3 normal;
};

float2 uberBasePolarUv(float3 direction)
{
    return float2(atan2(direction.z, direction.x) / 6.28318530718 + 0.5,
        acos(direction.y) / 3.14159265359);
}

float2 uberBaseUv(int mode, UberBaseMesh mesh)
{
    switch (mode)
    {
        case 1: return mesh.uv1;
        case 2: return mesh.uv2;
        case 3: return mesh.uv3;
        case 4: return uberBasePolarUv(reflect(-mesh.viewDirection, mesh.worldNormal));
        case 5: return mesh.worldPosition.xz;
        case 6: return uberBasePolarUv(mesh.worldNormal);
        case 8: return mesh.localPosition.xy;
        default: return mesh.uv0;
    }
}

float2 uberBaseTransformUv(float2 uv, float4 scaleOffset, float2 pan, float time)
{
    return uv * scaleOffset.xy + scaleOffset.zw + pan * time;
}

// The canonical fallback uses UV0 even if the bump map selects another UV.
// Native callers provide reconstructed screen derivatives for this same source.
UberBaseFrame uberBaseCotangentFrame(float3 normal, float3 positionDx,
    float3 positionDy, float2 uvDx, float2 uvDy)
{
    UberBaseFrame frame;
    frame.normal = normal;
    float determinant = uvDx.x * uvDy.y - uvDx.y * uvDy.x;
    if (abs(determinant) < 1e-6)
    {
        frame.tangent = normalize(abs(normal.z) < 0.999
            ? cross(normal, float3(0.0, 0.0, 1.0))
            : cross(normal, float3(0.0, 1.0, 0.0)));
        frame.bitangent = normalize(cross(normal, frame.tangent));
        return frame;
    }
    float3 positionDyPerpendicular = cross(positionDy, normal);
    float3 positionDxPerpendicular = cross(normal, positionDx);
    float3 tangent = positionDyPerpendicular * uvDx.x + positionDxPerpendicular * uvDy.x;
    float3 bitangent = positionDyPerpendicular * uvDx.y + positionDxPerpendicular * uvDy.y;
    float inverseMaximum = rsqrt(max(dot(tangent, tangent), dot(bitangent, bitangent)));
    frame.tangent = tangent * inverseMaximum;
    frame.bitangent = bitangent * inverseMaximum;
    return frame;
}

UberBaseFrame uberBaseImportedOrFallbackFrame(UberBaseMesh mesh,
    float3 positionDx, float3 positionDy, float2 uvDx, float2 uvDy)
{
    if (dot(mesh.tangent, mesh.tangent) <= 1e-8 || dot(mesh.bitangent, mesh.bitangent) <= 1e-8)
        return uberBaseCotangentFrame(mesh.vertexNormal, positionDx, positionDy, uvDx, uvDy);
    float3 tangent = mesh.tangent - mesh.vertexNormal * dot(mesh.vertexNormal, mesh.tangent);
    float lengthSquared = dot(tangent, tangent);
    if (lengthSquared <= 1e-8)
        return uberBaseCotangentFrame(mesh.vertexNormal, positionDx, positionDy, uvDx, uvDy);
    UberBaseFrame frame;
    frame.normal = mesh.vertexNormal;
    frame.tangent = tangent * rsqrt(lengthSquared);
    float handedness = dot(cross(mesh.vertexNormal, frame.tangent), mesh.bitangent) < 0.0 ? -1.0 : 1.0;
    frame.bitangent = cross(mesh.vertexNormal, frame.tangent) * handedness;
    return frame;
}

float3 uberBaseUnpackNormal(float4 sample, float scale)
{
    float2 xy = (sample.xy * 2.0 - 1.0) * scale;
    xy.y = -xy.y;
    // The canonical Uber path reconstructs Z from RG; sampled blue is unused.
    return float3(xy, sqrt(1.0 - saturate(dot(xy, xy))));
}

float3 uberBaseSobelNormal(float tl, float t, float tr, float l,
    float r, float bl, float b, float br, float scale)
{
    float slopeX = ((tl + 2.0 * l + bl) - (tr + 2.0 * r + br)) * 0.25;
    float slopeY = ((tl + 2.0 * t + tr) - (bl + 2.0 * b + br)) * 0.25;
    return normalize(float3(float2(slopeX, slopeY) * scale, 1.0));
}

float3 uberBaseMappedNormal(float3 tangentNormal, UberBaseFrame frame)
{
    tangentNormal.z = max(tangentNormal.z, 0.001);
    tangentNormal = normalize(tangentNormal);
    return normalize(frame.tangent * tangentNormal.x + frame.bitangent * tangentNormal.y
        + frame.normal * tangentNormal.z);
}

float4 uberBaseColor(UberBaseMaterialParameters material, float4 mainSample, float4 vertexColor)
{
    float4 color = mainSample * material._Color;
    if (material._MainVertexColoringEnabled > 0.5)
    {
        // Preserve the implementation, including the misleading historical
        // LinearSpace property name: a positive value performs this decode.
        if (material._MainVertexColoringLinearSpace > 0.5)
            vertexColor.rgb = vertexColor.rgb * (vertexColor.rgb
                * (vertexColor.rgb * 0.305306011 + 0.682171111) + 0.012522878);
        color.rgb *= lerp(float3(1.0), vertexColor.rgb, material._MainVertexColoring);
        color.a *= lerp(1.0, vertexColor.a, material._MainUseVertexColorAlpha);
    }
    return color;
}

float uberBaseAlpha(UberBaseMaterialParameters material, uint features, float baseAlpha, float4 alphaSample)
{
    float alpha = baseAlpha;
    if ((features & UberBaseAlphaMasks) != 0u && material._MainAlphaMaskMode > 0)
    {
        // With surface-extensions excluded the canonical alpha mask is red.
        float mask = alphaSample.r;
        if (material._AlphaMaskInvert > 0.5) mask = 1.0 - mask;
        mask = mask * material._AlphaMaskBlendStrength + material._AlphaMaskValue;
        switch (material._MainAlphaMaskMode)
        {
            case 1: alpha = mask; break;
            case 2: alpha *= mask; break;
            case 3: alpha = saturate(alpha + mask); break;
            case 4: alpha = saturate(alpha - mask); break;
        }
    }
    alpha = saturate(alpha + material._AlphaMod);
    return material._AlphaForceOpaque > 0.5 ? 1.0 : alpha;
}

float3 uberBaseRms(UberBaseMaterialParameters material, uint features,
    float4 metallicSample, float4 smoothnessSample, float4 specularSample)
{
    float metallic = 0.0;
    float roughness = clamp(1.0 - material._SpecularSmoothness, 0.04, 1.0);
    float specularMask = 1.0;
    if ((features & UberBaseAdvancedSpecular) != 0u)
    {
        metallic = metallicSample[clamp(material._PBRMetallicMapsMetallicChannel, 0, 3)];
        if (material._PBRMetallicMapInvert > 0.5) metallic = 1.0 - metallic;
        metallic = saturate(metallic * material._PBRMetallicMultiplier);
        float smoothness = smoothnessSample[clamp(material._PBRSmoothnessMapsChannel, 0, 3)];
        if (material._PBRSmoothnessMapInvert > 0.5) smoothness = 1.0 - smoothness;
        roughness = clamp(1.0 - smoothness * material._PBRRoughnessMultiplier, 0.04, 1.0);
        specularMask = saturate(max(max(specularSample.r, specularSample.g), max(specularSample.b, specularSample.a)));
    }
    // The admitted realistic source resets F0 after buildSurfacePbrData, then
    // returns zero from calculateAdvancedSpecular. SpecularTint does not reach
    // either final lobe in this specialization. Preserve that source behavior.
    return float3(roughness, metallic, max(material._SpecularStrength * specularMask, 0.0));
}

float2 uberBaseEmissionUv(UberBaseMaterialParameters material, UberBaseMesh mesh, float time)
{
    float2 uv = uberBaseTransformUv(uberBaseUv(material._EmissionMapUV, mesh),
        material._EmissionMap_ST, material._EmissionMapPan, time);
    if (material._EmissionScrollingEnabled > 0.5)
    {
        uv += material._EmissionScrollingSpeed * time;
        uv += mesh.vertexColor.rg * material._EmissionScrollingVertexColor;
    }
    return uv;
}

float3 uberBaseEmissionColor(UberBaseMaterialParameters material, uint features, float4 emissionSample)
{
    return (features & UberBaseEmission) != 0u
        ? emissionSample.rgb * material._EmissionColor.rgb * material._EmissionStrength : float3(0.0);
}

bool uberBaseCovered(UberBaseMaterialParameters material, float alpha)
{
    return material._Mode != 1 || alpha >= material._Cutoff;
}

float uberBaseOutputAlpha(UberBaseMaterialParameters material, float alpha)
{
    return material._Mode == 0 || material._Mode == 1 ? 1.0 : alpha;
}

#endif
