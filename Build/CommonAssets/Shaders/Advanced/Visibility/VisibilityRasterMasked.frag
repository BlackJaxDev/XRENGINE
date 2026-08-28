#version 450

#include "VisibilityInterface.glslinc"

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

const uint COVERAGE_TEXTURE_BINDING = 0u;
const uint ALPHA_CUTOFF_WORD = 0u;
const uint UV_SCALE_X_WORD = 1u;
const uint UV_SCALE_Y_WORD = 2u;
const uint UV_BIAS_X_WORD = 3u;
const uint UV_BIAS_Y_WORD = 4u;

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
    if (material.textureReferenceCount <=
            COVERAGE_TEXTURE_BINDING)
    {
        atomicAdd(XR_ADV_VisibilityCounters.decodeOutOfBounds, 1u);
        discard;
    }

    XRAdvancedMaterialTextureBinding coverageBinding =
        XR_ADV_LoadMaterialTextureBinding(
            material,
            COVERAGE_TEXTURE_BINDING);
    XRAdvancedEncodedTextureReference coverageTexture;
    XR_ADV_TryResolveTextureReference(
        coverageBinding,
        coverageTexture);

    float alphaCutoff = material.constantWordCount > ALPHA_CUTOFF_WORD
        ? uintBitsToFloat(XR_ADV_LoadMaterialConstant(
            material,
            ALPHA_CUTOFF_WORD))
        : 0.5;
    vec4 uvScaleBias = vec4(1.0, 1.0, 0.0, 0.0);
    if (material.constantWordCount > UV_BIAS_Y_WORD)
    {
        uvScaleBias = vec4(
            uintBitsToFloat(XR_ADV_LoadMaterialConstant(
                material,
                UV_SCALE_X_WORD)),
            uintBitsToFloat(XR_ADV_LoadMaterialConstant(
                material,
                UV_SCALE_Y_WORD)),
            uintBitsToFloat(XR_ADV_LoadMaterialConstant(
                material,
                UV_BIAS_X_WORD)),
            uintBitsToFloat(XR_ADV_LoadMaterialConstant(
                material,
                UV_BIAS_Y_WORD)));
    }

    vec2 coverageUv =
        VisibilityCoverageUv * uvScaleBias.xy +
        uvScaleBias.zw;
    if (XR_ADV_SampleTexture2D(
            coverageTexture,
            coverageUv).a < alphaCutoff)
    {
        discard;
    }

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
