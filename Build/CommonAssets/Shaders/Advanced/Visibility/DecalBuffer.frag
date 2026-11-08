#version 450

// Mesh surface decal: writes premultiplied albedo and roughness/metallic into the decal buffer.
// The pipeline blends with ONE / ONE_MINUS_SRC_ALPHA for color and alpha, so alpha accumulates
// coverage and overlapping decals compose in draw order. Native shading resolves the result as
// surface * (1 - coverage) + decal before lighting.

layout(location = 3) flat in uint VisibilityViewIndex;
layout(location = 6) in vec2 VisibilityCoverageUv;
layout(location = 9) flat in uint VisibilityMaterialDenseIndex;
layout(location = 12) in vec4 VisibilityCoverageColor;

#include "Advanced/Shading/StandardMaterial.glslinc"

layout(location = 0) out vec4 OutDecalAlbedo;
layout(location = 1) out vec4 OutDecalSurface;

void main()
{
    // Derivatives are taken before any discard.
    vec2 uv = VisibilityCoverageUv;
    vec2 uvDx = dFdx(uv);
    vec2 uvDy = dFdy(uv);
    if (VisibilityMaterialDenseIndex == XR_ADV_INVALID_DENSE_INDEX ||
        VisibilityMaterialDenseIndex >= uint(XR_ADV_Materials.records.length()))
        discard;

    XRAdvancedMaterialRecord material = XR_ADV_LoadMaterial(VisibilityMaterialDenseIndex);
    if (!XR_ADV_IsStandardMaterial(material))
        discard;

    uint flags = XR_ADV_LoadMaterialConstant(material, XR_ADV_STANDARD_FLAGS_WORD);
    vec4 base = XR_ADV_StandardVector(material, XR_ADV_STANDARD_BASE_COLOR_WORD) * VisibilityCoverageColor;
    if ((flags & 1u) != 0u)
        base *= XR_ADV_StandardTexture(material, 0u, uv, uvDx, uvDy, vec4(1.0));
    float coverage = base.a;
    // A separate opacity map multiplies coverage by its red channel, as in the masked raster.
    if ((flags & XR_ADV_STANDARD_OPACITY_TEXTURE_FLAG) != 0u)
        coverage *= XR_ADV_StandardTexture(material, XR_ADV_STANDARD_OPACITY_TEXTURE_SLOT,
            uv, uvDx, uvDy, vec4(1.0)).r;
    coverage = clamp(coverage, 0.0, 1.0);
    if (!(coverage >= 1.0 / 255.0))
        discard;

    vec4 rmse = XR_ADV_StandardVector(material, XR_ADV_STANDARD_RMSE_WORD);
    if ((flags & 4u) != 0u)
        rmse.xy *= XR_ADV_StandardTexture(material, 2u, uv, uvDx, uvDy, vec4(1.0)).rg;
    float roughness = clamp(rmse.x, 0.04, 1.0);
    float metallic = clamp(rmse.y, 0.0, 1.0);

    OutDecalAlbedo = vec4(clamp(base.rgb, vec3(0.0), vec3(1.0)) * coverage, coverage);
    OutDecalSurface = vec4(roughness * coverage, metallic * coverage, 0.0, coverage);
}
