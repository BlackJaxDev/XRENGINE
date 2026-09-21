#ifndef XR_DDGI_MATERIALS_INCLUDED
#define XR_DDGI_MATERIALS_INCLUDED
#pragma snippet "DDGIBindings"

#ifndef DDGI_MATERIAL_BINDING
#define DDGI_MATERIAL_BINDING 5
#endif
#ifndef DDGI_ATTRIBUTE_BINDING
#define DDGI_ATTRIBUTE_BINDING 7
#endif
#ifndef DDGI_MATERIAL_TEXTURE_BINDING
#define DDGI_MATERIAL_TEXTURE_BINDING 2
#endif

struct DDGIMaterialMap
{
    vec4 source;
    vec4 uvScaleOffset;
    vec4 wrapRotation;
};

// Matches DDGIMaterialGpu (416 bytes).
struct DDGIMaterial
{
    vec4 baseColorOpacity;
    vec4 emissiveMetallic;
    vec4 surface;
    vec4 transmission;
    vec4 emissionOptions;
    DDGIMaterialMap baseColorMap;
    DDGIMaterialMap opacityMap;
    DDGIMaterialMap normalMap;
    DDGIMaterialMap metallicMap;
    DDGIMaterialMap roughnessMap;
    DDGIMaterialMap emissiveMap;
    DDGIMaterialMap transmissionMap;
};

struct DDGITriangleAttributes
{
    vec4 normal0;
    vec4 normal1;
    vec4 normal2;
    vec4 uv01_0;
    vec4 uv01_1;
    vec4 uv01_2;
};

layout(std430, XR_DDGI_STORAGE_BINDING(DDGI_MATERIAL_BINDING)) readonly buffer GeometryMaterials
{
    DDGIMaterial gMaterials[];
};
layout(std430, XR_DDGI_STORAGE_BINDING(DDGI_ATTRIBUTE_BINDING)) readonly buffer TriangleAttributes
{
    DDGITriangleAttributes gAttributes[];
};
layout(XR_DDGI_SAMPLER_BINDING(DDGI_MATERIAL_TEXTURE_BINDING)) uniform sampler2DArray uMaterialTextures;
uniform uint uMaterialCount;

vec4 DDGIHitUvs(PackedTriangle triangle, vec3 barycentric)
{
    uint index = triangle.extra.w;
    if (index >= uint(gAttributes.length()))
        return vec4(0.0);
    DDGITriangleAttributes a = gAttributes[index];
    return a.uv01_0 * barycentric.x + a.uv01_1 * barycentric.y + a.uv01_2 * barycentric.z;
}

vec2 DDGIMapUv(DDGIMaterialMap map, vec4 uv01)
{
    vec2 uv = map.source.y < 0.5 ? uv01.xy : uv01.zw;
    uv *= map.uvScaleOffset.xy;
    uv = mat2(map.wrapRotation.w, map.wrapRotation.z, -map.wrapRotation.z, map.wrapRotation.w) * uv;
    return uv + map.uvScaleOffset.zw;
}

float DDGIWrap(float uv, float mode)
{
    if (mode < 0.5)
        return fract(uv);
    if (mode > 2.5)
        return 1.0 - abs(mod(uv, 2.0) - 1.0);
    return clamp(uv, 0.0, 1.0);
}

vec4 DDGISampleMap(DDGIMaterialMap map, vec4 uv01, vec4 missing)
{
    if (map.source.x < 0.0)
        return missing;
    vec2 uv = DDGIMapUv(map, uv01);
    if ((map.wrapRotation.x > 0.5 && map.wrapRotation.x < 1.5 && (uv.x < 0.0 || uv.x > 1.0)) ||
        (map.wrapRotation.y > 0.5 && map.wrapRotation.y < 1.5 && (uv.y < 0.0 || uv.y > 1.0)))
        return vec4(0.0);
    uv = vec2(DDGIWrap(uv.x, map.wrapRotation.x), DDGIWrap(uv.y, map.wrapRotation.y));
    vec4 result = textureLod(uMaterialTextures, vec3(uv, map.source.x), 0.0);
    if (map.source.w > 0.5)
        result.rgb = mix(result.rgb / 12.92, pow(max((result.rgb + 0.055) / 1.055, vec3(0.0)), vec3(2.4)),
            greaterThan(result.rgb, vec3(0.04045)));
    return result;
}

float DDGISampleScalar(DDGIMaterialMap map, vec4 uv01)
{
    vec4 value = DDGISampleMap(map, uv01, vec4(1.0));
    return value[clamp(int(map.source.z), 0, 3)];
}

float DDGICoverage(DDGIMaterial material, vec4 uv01)
{
    if (material.surface.w < 0.5)
        return 1.0;
    float alpha = clamp(material.baseColorOpacity.a * DDGISampleMap(material.baseColorMap, uv01, vec4(1.0)).a
        * DDGISampleScalar(material.opacityMap, uv01), 0.0, 1.0);
    return material.surface.w < 1.5 ? step(material.surface.z, alpha) : alpha;
}

bool DDGIAcceptSurface(PackedTriangle triangle, vec3 barycentric)
{
    if (triangle.extra.x >= min(uMaterialCount, uint(gMaterials.length())))
        return false;
    return DDGICoverage(gMaterials[triangle.extra.x], DDGIHitUvs(triangle, barycentric)) > 0.001;
}

vec3 DDGITransmission(DDGIMaterial material, vec4 uv01, float coverage)
{
    float transmission = clamp(material.transmission.a * DDGISampleScalar(material.transmissionMap, uv01), 0.0, 1.0);
    // Thin-sheet transmission is straight through; coverage represents uncovered
    // area while transmission tints the covered part. No refractive caustics.
    return vec3(1.0 - coverage) + coverage * transmission * clamp(material.transmission.rgb, vec3(0.0), vec3(1.0));
}

vec3 DDGIShadingNormal(PackedTriangle triangle, vec3 barycentric, DDGIMaterial material, vec4 uv01, vec3 incoming)
{
    vec3 e1 = triangle.v1.xyz - triangle.v0.xyz;
    vec3 e2 = triangle.v2.xyz - triangle.v0.xyz;
    vec3 geometric = normalize(cross(e1, e2));
    vec3 normal = geometric;
    uint index = triangle.extra.w;
    if (index < uint(gAttributes.length()))
    {
        DDGITriangleAttributes a = gAttributes[index];
        vec3 interpolated = a.normal0.xyz * barycentric.x + a.normal1.xyz * barycentric.y + a.normal2.xyz * barycentric.z;
        if (dot(interpolated, interpolated) > 1e-8)
            normal = normalize(interpolated);
        if (material.normalMap.source.x >= 0.0)
        {
            vec2 uv0 = DDGIMapUv(material.normalMap, a.uv01_0);
            vec2 uv1 = DDGIMapUv(material.normalMap, a.uv01_1);
            vec2 uv2 = DDGIMapUv(material.normalMap, a.uv01_2);
            vec2 d1 = uv1 - uv0;
            vec2 d2 = uv2 - uv0;
            float determinant = d1.x * d2.y - d1.y * d2.x;
            if (abs(determinant) > 1e-8)
            {
                vec3 tangent = (e1 * d2.y - e2 * d1.y) / determinant;
                tangent -= normal * dot(normal, tangent);
                if (dot(tangent, tangent) > 1e-8)
                {
                    tangent = normalize(tangent);
                    vec3 sourceBitangent = (e2 * d1.x - e1 * d2.x) / determinant;
                    vec3 bitangent = normalize(cross(normal, tangent));
                    if (dot(bitangent, sourceBitangent) < 0.0)
                        bitangent = -bitangent;
                    vec3 mapped = DDGISampleMap(material.normalMap, uv01, vec4(0.5, 0.5, 1.0, 1.0)).xyz * 2.0 - 1.0;
                    mapped.xy *= material.surface.y;
                    if (dot(mapped, mapped) > 1e-8)
                        normal = normalize(mat3(tangent, bitangent, normal) * mapped);
                }
            }
        }
    }
    // Keep shading normals in the visible geometric hemisphere.
    if (dot(geometric, -incoming) < 0.0)
        geometric = -geometric;
    if (dot(normal, geometric) < 0.0)
        normal = -normal;
    return normal;
}

#endif
