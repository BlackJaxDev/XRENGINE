#version 450

// Masked directional shadow casters: the standard-material alpha test the
// visibility raster applies, then fixed-function depth only.
layout(location = 0) in vec2 ShadowCoverageUv;
layout(location = 1) flat in uint ShadowMaterialDenseIndex;

#define XR_ADV_VISIBILITY_COUNTER_INDEX 0u
#include "Advanced/Shading/StandardMaterial.glslinc"

void main()
{
    if (ShadowMaterialDenseIndex == XR_ADV_INVALID_DENSE_INDEX ||
        ShadowMaterialDenseIndex >= uint(XR_ADV_Materials.records.length()))
    {
        atomicAdd(XR_ADV_VisibilityCounters.decodeOutOfBounds, 1u);
        discard;
    }

    XRAdvancedMaterialRecord material =
        XR_ADV_LoadMaterial(ShadowMaterialDenseIndex);
    if (!XR_ADV_IsStandardMaterial(material))
    {
        atomicAdd(XR_ADV_VisibilityCounters.decodeOutOfBounds, 1u);
        discard;
    }

    uint flags = XR_ADV_LoadMaterialConstant(material, XR_ADV_STANDARD_FLAGS_WORD);
    float alpha = XR_ADV_StandardVector(material, XR_ADV_STANDARD_BASE_COLOR_WORD).a;
    if ((flags & 1u) != 0u)
        alpha *= XR_ADV_StandardTexture(material, 0u, ShadowCoverageUv,
            dFdx(ShadowCoverageUv), dFdy(ShadowCoverageUv), vec4(1.0)).a;
    float alphaCutoff = uintBitsToFloat(XR_ADV_LoadMaterialConstant(material, XR_ADV_STANDARD_ALPHA_CUTOFF_WORD));
    if (alpha < alphaCutoff)
        discard;
}
