#version 450

#include "Advanced/Shading/StandardMaterial.glslinc"

layout(location = 0) flat in uint VisibilityDrawIndex;
layout(location = 1) flat in uint VisibilitySelectionId;
layout(location = 2) flat in uint VisibilityProducer;
layout(location = 3) flat in uint VisibilityViewIndex;
layout(location = 4) flat in uint VisibilityOrigin;
layout(location = 5) flat in uint VisibilityVelocityValid;
layout(location = 6) in vec2 VisibilityCoverageUv;
layout(location = 7) flat in uint VisibilityPrimitiveBase;
layout(location = 8) flat in uint VisibilityMeshletIndex;
layout(location = 9) flat in uint VisibilityMaterialDenseIndex;

layout(location = 0) out uvec2 OutVisibilityIdentity;
layout(location = 1) out uint OutVisibilityMetadata;
layout(location = 2) out uint OutVisibilitySelection;

void main()
{
    if (VisibilityMaterialDenseIndex ==
            XR_ADV_INVALID_DENSE_INDEX ||
        VisibilityMaterialDenseIndex >=
            uint(XR_ADV_Materials.records.length()))
    {
        atomicAdd(XR_ADV_VisibilityCounters.decodeOutOfBounds, 1u);
        discard;
    }

    XRAdvancedMaterialRecord material =
        XR_ADV_LoadMaterial(VisibilityMaterialDenseIndex);
    if (!XR_ADV_IsStandardMaterial(material))
    {
        atomicAdd(XR_ADV_VisibilityCounters.decodeOutOfBounds, 1u);
        discard;
    }
    uint flags = XR_ADV_LoadMaterialConstant(material, XR_ADV_STANDARD_FLAGS_WORD);
    float alpha = XR_ADV_StandardVector(material, XR_ADV_STANDARD_BASE_COLOR_WORD).a;
    if ((flags & 1u) != 0u)
        alpha *= XR_ADV_StandardTexture(material, 0u, VisibilityCoverageUv,
            dFdx(VisibilityCoverageUv), dFdy(VisibilityCoverageUv), vec4(1.0)).a;
    float alphaCutoff = uintBitsToFloat(XR_ADV_LoadMaterialConstant(material, XR_ADV_STANDARD_ALPHA_CUTOFF_WORD));
    if (alpha < alphaCutoff) discard;
    uint primitive = XR_ADV_EncodeVisibilityPrimitive(
        VisibilityProducer,
        VisibilityPrimitiveBase,
        VisibilityMeshletIndex,
        uint(gl_PrimitiveID));
    if (VisibilityDrawIndex == 0u ||
        VisibilityDrawIndex == XR_ADV_VIS_INVALID ||
        primitive == XR_ADV_VIS_INVALID ||
        !XR_ADV_IsSupportedVisibilityProducer(VisibilityProducer) ||
        VisibilityOrigin > 1u ||
        VisibilityViewIndex > 0xFFu)
    {
        atomicAdd(XR_ADV_VisibilityCounters.payloadOverflow, 1u);
        discard;
    }

    bool selectionValid =
        VisibilitySelectionId != XR_ADV_VIS_INVALID;
    OutVisibilityIdentity =
        uvec2(VisibilityDrawIndex, primitive);
    OutVisibilityMetadata = XR_ADV_PackVisibilityMetadata(
        VisibilityProducer,
        VisibilityOrigin,
        true,
        gl_FrontFacing,
        VisibilityVelocityValid != 0u,
        VisibilityViewIndex,
        selectionValid);
    OutVisibilitySelection = selectionValid
        ? VisibilitySelectionId
        : XR_ADV_VIS_INVALID;
    atomicAdd(
        XR_ADV_VisibilityCounters.maskedCoveragePixels,
        1u);
}
