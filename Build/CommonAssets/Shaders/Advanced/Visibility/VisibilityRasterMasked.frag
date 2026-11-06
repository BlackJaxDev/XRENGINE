#version 450

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
layout(location = 10) flat in uint VisibilityEditorFlags;
layout(location = 11) in vec2 VisibilityCoverageUv1;
layout(location = 12) in vec4 VisibilityCoverageColor;
layout(location = 13) in vec3 VisibilityCoverageWorldPosition;
layout(location = 14) in vec3 VisibilityCoverageLocalPosition;
layout(location = 15) in vec2 VisibilityCoverageUv2;
layout(location = 16) in vec2 VisibilityCoverageUv3;

#define XR_ADV_VISIBILITY_COUNTER_INDEX VisibilityViewIndex
#include "Advanced/Shading/NativeUberCoverage.glslinc"
#include "Advanced/Shading/NativeUberPrimitiveCoverage.glslinc"
#if defined(XR_ADV_VISIBILITY_FRAGMENT_DEPTH)
#include "Advanced/Shading/StandardParallax.glslinc"
#endif

layout(location = 0) out uvec2 OutVisibilityIdentity;
layout(location = 1) out uint OutVisibilityMetadata;
layout(location = 2) out uint OutVisibilitySelection;
layout(location = 3) out vec2 OutVisibilitySamplePosition;

#if defined(XR_ADV_VISIBILITY_FRAGMENT_DEPTH)
// Parallax occlusion for standard height-map materials: discards silhouette fragments and
// moves the fragment depth to the height-field hit along the view ray. Shading recovers the
// parallax UV from that depth (Advanced/Shading/StandardParallax.glslinc).
void XR_ADV_ApplyVisibilityParallax(XRAdvancedMaterialRecord material, uint flags,
    vec3 positionDx, vec3 positionDy, vec2 uvDx, vec2 uvDy)
{
    XRAdvancedViewRecord view = XR_ADV_LoadView(VisibilityViewIndex);
    vec3 position = VisibilityCoverageWorldPosition;
    vec3 V = normalize(view.cameraPositionAndNear.xyz - position);
    vec3 N = normalize(cross(positionDx, positionDy));
    if (dot(N, V) < 0.0)
        N = -N;
    XRAdvancedParallaxFrame frame = XR_ADV_StandardParallaxFrame(material,
        positionDx, positionDy, uvDx, uvDy, N, V);
    float hitDistance;
    bool outsideUnitSquare;
    XR_ADV_StandardParallaxMarch(material, flags, VisibilityCoverageUv, uvDx, uvDy,
        frame, hitDistance, outsideUnitSquare);
    if (outsideUnitSquare)
        discard;
    if (!(hitDistance > 0.0))
        return;

    vec4 baseClip = vec4(position, 1.0) * view.viewProjectionJittered;
    vec4 hitClip = vec4(position - V * hitDistance, 1.0) * view.viewProjectionJittered;
    float windowScale = (view.flags & XR_ADV_VIEW_DEPTH_ZERO_TO_ONE) != 0u ? 1.0 : 0.5;
    float depthDelta = (hitClip.z / hitClip.w - baseClip.z / baseClip.w) * windowScale;
    if (!isnan(depthDelta) && !isinf(depthDelta))
        gl_FragDepth = clamp(gl_FragCoord.z + depthDelta, 0.0, 1.0);
}
#endif

void main()
{
#if defined(XR_ADV_VISIBILITY_FRAGMENT_DEPTH)
    // This variant writes depth on every path. Derivatives are taken before any discard.
    gl_FragDepth = gl_FragCoord.z;
    vec3 parallaxPositionDx = dFdx(VisibilityCoverageWorldPosition);
    vec3 parallaxPositionDy = dFdy(VisibilityCoverageWorldPosition);
    vec2 parallaxUvDx = dFdx(VisibilityCoverageUv);
    vec2 parallaxUvDy = dFdy(VisibilityCoverageUv);
#endif
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
        uint coveragePrimitive = XR_ADV_EncodeVisibilityPrimitive(
            VisibilityProducer, VisibilityPrimitiveBase,
            VisibilityMeshletIndex, uint(gl_PrimitiveID));
        if (!XR_UBER_KeepOriginalTriangle(VisibilityDrawIndex,
                VisibilityProducer, coveragePrimitive)) discard;
        XRUberCoordinates coordinates;
        coordinates.uv0 = VisibilityCoverageUv;
        coordinates.uv1 = VisibilityCoverageUv1;
        coordinates.uv2 = VisibilityCoverageUv2;
        coordinates.uv3 = VisibilityCoverageUv3;
        coordinates.uv0Dx = dFdx(VisibilityCoverageUv);
        coordinates.uv0Dy = dFdy(VisibilityCoverageUv);
        coordinates.uv1Dx = dFdx(VisibilityCoverageUv1);
        coordinates.uv1Dy = dFdy(VisibilityCoverageUv1);
        coordinates.uv2Dx = dFdx(VisibilityCoverageUv2);
        coordinates.uv2Dy = dFdy(VisibilityCoverageUv2);
        coordinates.uv3Dx = dFdx(VisibilityCoverageUv3);
        coordinates.uv3Dy = dFdy(VisibilityCoverageUv3);
        coordinates.worldPosition = VisibilityCoverageWorldPosition;
        coordinates.localPosition = VisibilityCoverageLocalPosition;
        coordinates.color = VisibilityCoverageColor;
        coordinates.frontFacing = gl_FrontFacing;
        coordinates.time = uintBitsToFloat(
            XR_ADV_LoadView(VisibilityViewIndex).renderTimeBits);
        vec4 baseColor;
        vec3 edgeEmission;
        if (!XR_UBER_ApplyCoverage(coordinates, baseColor, edgeEmission)) discard;
    }
    else
    {
        uint flags = XR_ADV_LoadMaterialConstant(material, XR_ADV_STANDARD_FLAGS_WORD);
        // Parallax materials publish the masked row for their depth-writing raster. They
        // alpha-test only when they are also masked (row flag bit 3).
        bool parallax = (flags & XR_ADV_STANDARD_PARALLAX_FLAG) != 0u &&
            (flags & XR_ADV_STANDARD_HEIGHT_MAP_FLAG) != 0u;
        if (!parallax || (flags & 8u) != 0u)
        {
            float alpha = XR_ADV_StandardVector(material, XR_ADV_STANDARD_BASE_COLOR_WORD).a;
            if ((flags & 1u) != 0u)
                alpha *= XR_ADV_StandardTexture(material, 0u, VisibilityCoverageUv,
                    dFdx(VisibilityCoverageUv), dFdy(VisibilityCoverageUv), vec4(1.0)).a;
            float alphaCutoff = uintBitsToFloat(XR_ADV_LoadMaterialConstant(material, XR_ADV_STANDARD_ALPHA_CUTOFF_WORD));
            if (alpha < alphaCutoff) discard;
        }
#if defined(XR_ADV_VISIBILITY_FRAGMENT_DEPTH)
        if (parallax)
            XR_ADV_ApplyVisibilityParallax(material, flags,
                parallaxPositionDx, parallaxPositionDy, parallaxUvDx, parallaxUvDy);
#endif
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
        selectionValid,
        VisibilityEditorFlags);
    OutVisibilitySelection = selectionValid
        ? VisibilitySelectionId
        : XR_ADV_VIS_INVALID;
    // Preserve the actual covered sample position for native MSAA shading.
    OutVisibilitySamplePosition = gl_SamplePosition;
    atomicAdd(
        XR_ADV_VisibilityCounters.maskedCoveragePixels,
        1u);
}
