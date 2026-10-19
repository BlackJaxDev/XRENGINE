#version 450

// Masked directional shadow casters: the standard-material alpha test the
// visibility raster applies, then fixed-function depth only.
layout(location = 0) in vec2 ShadowCoverageUv;
layout(location = 1) flat in uint ShadowMaterialDenseIndex;
layout(location = 2) in vec2 ShadowCoverageUv1;
layout(location = 3) in vec4 ShadowCoverageColor;
layout(location = 4) in vec3 ShadowCoverageWorldPosition;
layout(location = 5) in vec3 ShadowCoverageLocalPosition;
layout(location = 6) in vec2 ShadowCoverageUv2;
layout(location = 7) in vec2 ShadowCoverageUv3;
layout(location = 8) flat in uint ShadowDrawIndex;
layout(location = 9) flat in uint ShadowProducer;
layout(location = 10) flat in uint ShadowPrimitiveBase;

#define XR_ADV_VISIBILITY_COUNTER_INDEX 0u
#include "Advanced/Shading/NativeUberCoverage.glslinc"
#include "Advanced/Shading/NativeUberPrimitiveCoverage.glslinc"

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
    bool standard = XR_ADV_IsStandardMaterial(material);
    bool uber = XR_UBER_IsMaterial(material);
    if (!standard && !uber)
    {
        atomicAdd(XR_ADV_VisibilityCounters.decodeOutOfBounds, 1u);
        discard;
    }

    if (uber)
    {
        XR_UBER_Material = material;
        uint primitive = XR_ADV_EncodeVisibilityPrimitive(
            ShadowProducer, ShadowPrimitiveBase, XR_ADV_VIS_INVALID,
            uint(gl_PrimitiveID));
        if (!XR_UBER_KeepOriginalTriangle(ShadowDrawIndex,
                ShadowProducer, primitive)) discard;
        XRUberCoordinates coordinates;
        coordinates.uv0 = ShadowCoverageUv;
        coordinates.uv1 = ShadowCoverageUv1;
        coordinates.uv2 = ShadowCoverageUv2;
        coordinates.uv3 = ShadowCoverageUv3;
        coordinates.uv0Dx = dFdx(ShadowCoverageUv);
        coordinates.uv0Dy = dFdy(ShadowCoverageUv);
        coordinates.uv1Dx = dFdx(ShadowCoverageUv1);
        coordinates.uv1Dy = dFdy(ShadowCoverageUv1);
        coordinates.uv2Dx = dFdx(ShadowCoverageUv2);
        coordinates.uv2Dy = dFdy(ShadowCoverageUv2);
        coordinates.uv3Dx = dFdx(ShadowCoverageUv3);
        coordinates.uv3Dy = dFdy(ShadowCoverageUv3);
        coordinates.worldPosition = ShadowCoverageWorldPosition;
        coordinates.localPosition = ShadowCoverageLocalPosition;
        coordinates.color = ShadowCoverageColor;
        coordinates.frontFacing = gl_FrontFacing;
        coordinates.time = uintBitsToFloat(
            XR_ADV_LoadView(0u).renderTimeBits);
        vec4 baseColor;
        vec3 edgeEmission;
        if (!XR_UBER_ApplyCoverage(coordinates, baseColor, edgeEmission)) discard;
    }
    else
    {
        uint flags = XR_ADV_LoadMaterialConstant(material, XR_ADV_STANDARD_FLAGS_WORD);
        float alpha = XR_ADV_StandardVector(material, XR_ADV_STANDARD_BASE_COLOR_WORD).a;
        if ((flags & 1u) != 0u)
            alpha *= XR_ADV_StandardTexture(material, 0u, ShadowCoverageUv,
                dFdx(ShadowCoverageUv), dFdy(ShadowCoverageUv), vec4(1.0)).a;
        float alphaCutoff = uintBitsToFloat(XR_ADV_LoadMaterialConstant(material, XR_ADV_STANDARD_ALPHA_CUTOFF_WORD));
        if (alpha < alphaCutoff) discard;
    }
}
