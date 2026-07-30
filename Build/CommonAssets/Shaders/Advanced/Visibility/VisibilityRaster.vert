#version 450

#if defined(XR_ADV_BACKEND_OPENGL)
#extension GL_ARB_shader_draw_parameters : require
#endif

#include "VisibilityInterface.glslinc"

layout(location = 0) in vec3 Position;
layout(location = 1) in vec2 TexCoord0;

layout(location = 0) flat out uint VisibilityDrawIndex;
layout(location = 1) flat out uint VisibilitySelectionId;
layout(location = 2) flat out uint VisibilityProducer;
layout(location = 3) flat out uint VisibilityViewIndex;
layout(location = 4) flat out uint VisibilityOrigin;
layout(location = 5) flat out uint VisibilityVelocityValid;
layout(location = 6) out vec2 VisibilityCoverageUv;
layout(location = 7) flat out uint VisibilityPrimitiveBase;
layout(location = 8) flat out uint VisibilityMeshletIndex;
layout(location = 9) flat out uint VisibilityMaterialDenseIndex;

uniform mat4 JitteredViewProjection;
uniform mat4 UnjitteredViewProjection;
uniform mat4 PreviousUnjitteredViewProjection;
uniform uint CpuDirectPayloadIndex;
uniform uint UseIndirectPayloadIndex;
uniform uint VisibilityProducerClass;
uniform uint VisibilityView;
uniform uint VisibilityPhaseOrigin;
uniform uint VisibilityVelocityIsValid;

uint XR_ADV_VisibilityPayloadIndex()
{
    if (UseIndirectPayloadIndex == 0u)
        return CpuDirectPayloadIndex;

#if defined(XR_ADV_BACKEND_OPENGL)
    return gl_BaseInstanceARB;
#else
    return gl_BaseInstance;
#endif
}

void XR_ADV_RejectVisibilityVertex()
{
    gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
    VisibilityDrawIndex = XR_ADV_VIS_INVALID;
    VisibilitySelectionId = XR_ADV_VIS_INVALID;
    VisibilityProducer = VisibilityProducerClass;
    VisibilityViewIndex = VisibilityView;
    VisibilityOrigin = VisibilityPhaseOrigin;
    VisibilityVelocityValid = 0u;
    VisibilityCoverageUv = vec2(0.0);
    VisibilityPrimitiveBase = 0u;
    VisibilityMeshletIndex = XR_ADV_VIS_INVALID;
    VisibilityMaterialDenseIndex = XR_ADV_INVALID_DENSE_INDEX;
}

void main()
{
    uint payloadIndex = XR_ADV_VisibilityPayloadIndex();
    if (payloadIndex >= uint(XR_ADV_VisibilityPayloads.records.length()))
    {
        atomicAdd(XR_ADV_VisibilityCounters.decodeOutOfBounds, 1u);
        XR_ADV_RejectVisibilityVertex();
        return;
    }

    XRAdvancedVisibilityPayload payload =
        XR_ADV_VisibilityPayloads.records[payloadIndex];
    uint drawDense = XR_ADV_ResolveVisibilityHandle(
        payload.draw,
        VisibilityDrawLookupSegment,
        XR_ADV_DIAGNOSTIC_DRAW);
    uint geometryDense = XR_ADV_ResolveVisibilityHandle(
        payload.geometry,
        VisibilityGeometryLookupSegment,
        XR_ADV_DIAGNOSTIC_MESH);
    uint materialDense = XR_ADV_ResolveVisibilityHandle(
        payload.material,
        VisibilityMaterialLookupSegment,
        XR_ADV_DIAGNOSTIC_MATERIAL);
    if (drawDense == XR_ADV_INVALID_DENSE_INDEX ||
        geometryDense == XR_ADV_INVALID_DENSE_INDEX ||
        materialDense == XR_ADV_INVALID_DENSE_INDEX)
    {
        atomicAdd(XR_ADV_VisibilityCounters.decodeOutOfBounds, 1u);
        XR_ADV_RejectVisibilityVertex();
        return;
    }

    XRAdvancedDrawRecord draw = XR_ADV_LoadDraw(drawDense);
    uint instanceDense = XR_ADV_ResolveVisibilityHandle(
        draw.instance,
        VisibilityInstanceLookupSegment,
        XR_ADV_DIAGNOSTIC_INSTANCE);
    uint transformDense = XR_ADV_ResolveVisibilityHandle(
        draw.currentTransform,
        VisibilityTransformLookupSegment,
        XR_ADV_DIAGNOSTIC_DRAW);
    uint previousTransformDense = XR_ADV_ResolveVisibilityHandle(
        draw.previousTransform,
        VisibilityTransformLookupSegment,
        XR_ADV_DIAGNOSTIC_DRAW);
    if (instanceDense == XR_ADV_INVALID_DENSE_INDEX ||
        transformDense == XR_ADV_INVALID_DENSE_INDEX ||
        previousTransformDense == XR_ADV_INVALID_DENSE_INDEX)
    {
        atomicAdd(XR_ADV_VisibilityCounters.decodeOutOfBounds, 1u);
        XR_ADV_RejectVisibilityVertex();
        return;
    }

    XRAdvancedInstanceRecord instanceRecord =
        XR_ADV_LoadInstance(instanceDense);
    XRAdvancedGeometryRecord geometryRecord =
        XR_ADV_LoadGeometry(geometryDense);
    XRAdvancedTransformRecord transformRecord =
        XR_ADV_LoadTransform(transformDense);
    XRAdvancedTransformRecord previousTransformRecord =
        XR_ADV_LoadTransform(previousTransformDense);

    vec3 localPosition = Position;
#if defined(XR_ADV_VIS_VERTEX_DISPLACEMENT)
    localPosition += XR_ADV_ApplyVisibilityVertexDisplacement(
        payload,
        materialDense,
        Position,
        TexCoord0);
#endif

    vec4 worldPosition =
        vec4(localPosition, 1.0) * transformRecord.world;
    vec4 previousWorldPosition =
        vec4(localPosition, 1.0) * previousTransformRecord.world;
    gl_Position = worldPosition * JitteredViewProjection;

    // The unjittered pair deliberately remains part of this producer ABI.
    // Document 05 reconstructs the same pair from the stored draw/primitive.
    vec4 currentUnjitteredClip =
        worldPosition * UnjitteredViewProjection;
    vec4 previousUnjitteredClip =
        previousWorldPosition * PreviousUnjitteredViewProjection;
    VisibilityDrawIndex = payload.draw.index;
    VisibilityProducer = VisibilityProducerClass;
    VisibilityViewIndex = VisibilityView;
    VisibilityOrigin = VisibilityPhaseOrigin;
    VisibilityCoverageUv = TexCoord0;
    VisibilityPrimitiveBase = payload.firstIndex / 3u;
    VisibilityMeshletIndex = XR_ADV_VIS_INVALID;
    VisibilityMaterialDenseIndex = materialDense;

    bool geometrySourcesValid =
        geometryRecord.currentVertexData.elementCount != 0u &&
        geometryRecord.previousVertexData.elementCount != 0u;
    bool temporalInputsValid =
        !any(isnan(currentUnjitteredClip)) &&
        !any(isinf(currentUnjitteredClip)) &&
        !any(isnan(previousUnjitteredClip)) &&
        !any(isinf(previousUnjitteredClip));
    VisibilityVelocityValid =
        VisibilityVelocityIsValid != 0u &&
        geometrySourcesValid &&
        temporalInputsValid
            ? 1u
            : 0u;

    uint editorDense = XR_ADV_ResolveVisibilityHandle(
        draw.editorIdentity,
        VisibilityEditorIdentityLookupSegment,
        XR_ADV_DIAGNOSTIC_DRAW);
    VisibilitySelectionId =
        editorDense != XR_ADV_INVALID_DENSE_INDEX
            ? XR_ADV_LoadEditorIdentity(editorDense).selectionId
            : XR_ADV_VIS_INVALID;

    uvec2 activeMask = VisibilityView < 32u
        ? uvec2(1u << VisibilityView, 0u)
        : uvec2(0u, 1u << (VisibilityView - 32u));
    if (all(equal(
            uvec2(instanceRecord.viewMaskLow, instanceRecord.viewMaskHigh) &
                activeMask,
            uvec2(0u))))
    {
        XR_ADV_RejectVisibilityVertex();
    }
}
