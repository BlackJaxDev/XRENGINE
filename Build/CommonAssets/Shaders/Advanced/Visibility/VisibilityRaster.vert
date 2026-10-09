#version 450

#extension GL_ARB_shader_draw_parameters : require
#if defined(XR_ADV_MULTIVIEW_RASTER)
#extension GL_EXT_multiview : require
#endif

// Matches the mesh-shader visibility ABI. Indexed and meshlet submission must
// select the same immutable canonical view for a given raster invocation.
layout(push_constant, std430) uniform XRAdvancedVisibilityRasterPushConstants
{
    uint meshArgumentBase;
    uint producerAndOrigin;
    uint viewIndex;
    uint flags;
} XR_ADV_VisibilityRasterPush;

#if defined(XR_ADV_OPENGL_MULTIVIEW_RASTER)
layout(num_views = 2) in;
layout(std430, binding = 77) readonly buffer XRAdvancedStereoViewMasks { uint masks[]; } XR_ADV_StereoViewMasks;
#define XR_ADV_RASTER_VIEW_INDEX uint(gl_ViewID_OVR)
#else
#define XR_ADV_RASTER_VIEW_INDEX XR_ADV_VisibilityRasterPush.viewIndex
#endif
#define XR_ADV_VISIBILITY_COUNTER_INDEX XR_ADV_RASTER_VIEW_INDEX
#define XR_ADV_ENABLE_LATE_VISIBILITY_OUTPUTS 1
#include "VisibilityInterface.glslinc"

// Vulkan exposes gl_VertexIndex while desktop OpenGL names the identical
// indexed draw input gl_VertexID.
#if defined(XR_ADV_BACKEND_OPENGL)
#define XR_ADV_VERTEX_INDEX gl_VertexID
#else
#define XR_ADV_VERTEX_INDEX gl_VertexIndex
#endif

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
layout(location = 10) flat out uint VisibilityEditorFlags;

uint XR_ADV_VisibilityPayloadIndex()
{
    if ((XR_ADV_VisibilityRasterPush.producerAndOrigin &
            XR_ADV_VIS_PRODUCER_MASK) != XR_ADV_VIS_PRODUCER_INDIRECT_INDEXED)
        return gl_BaseInstanceARB;
#if defined(XR_ADV_BACKEND_OPENGL)
    uint instanceIndex = gl_BaseInstanceARB + uint(gl_InstanceID);
#else
    uint instanceIndex = uint(gl_InstanceIndex);
#endif
    bool late = (XR_ADV_VisibilityRasterPush.producerAndOrigin & 8u) != 0u;
    uint tableLength = late
        ? uint(XR_ADV_VisibilityLateMeshPayloads.records.length())
        : uint(XR_ADV_VisibilityMeshPayloads.records.length());
    if (gl_BaseInstanceARB >= tableLength)
        return XR_ADV_VIS_INVALID;
    uint firstPayload = late
        ? XR_ADV_VisibilityLateMeshPayloads.records[gl_BaseInstanceARB]
        : XR_ADV_VisibilityMeshPayloads.records[gl_BaseInstanceARB];
    if (firstPayload >= uint(XR_ADV_VisibilityPayloads.records.length()))
        return XR_ADV_VIS_INVALID;
    if (XR_ADV_VisibilityPayloads.records[firstPayload].instanceCount > 1u)
        return firstPayload;
    if (instanceIndex >= tableLength)
        return XR_ADV_VIS_INVALID;
    return late
        ? XR_ADV_VisibilityLateMeshPayloads.records[instanceIndex]
        : XR_ADV_VisibilityMeshPayloads.records[instanceIndex];
}

void XR_ADV_RejectVisibilityVertex()
{
    gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
    VisibilityDrawIndex = XR_ADV_VIS_INVALID;
    VisibilitySelectionId = XR_ADV_VIS_INVALID;
    VisibilityProducer = XR_ADV_VIS_PRODUCER_INDIRECT_INDEXED;
    VisibilityViewIndex = 0u;
    VisibilityOrigin = 0u;
    VisibilityVelocityValid = 0u;
    VisibilityCoverageUv = vec2(0.0);
    VisibilityPrimitiveBase = 0u;
    VisibilityMeshletIndex = XR_ADV_VIS_INVALID;
    VisibilityMaterialDenseIndex = XR_ADV_INVALID_DENSE_INDEX;
    VisibilityEditorFlags = 0u;
}

void main()
{
#if defined(XR_ADV_MULTIVIEW_RASTER)
    // Each indirect stream belongs to one eye. Multiview broadcasts a draw;
    // suppress other hardware views before fetching payloads or writing counters.
    if (uint(gl_ViewIndex) != XR_ADV_VisibilityRasterPush.viewIndex)
    {
        XR_ADV_RejectVisibilityVertex();
        return;
    }
#endif
    uint payloadIndex = XR_ADV_VisibilityPayloadIndex();
#if defined(XR_ADV_OPENGL_MULTIVIEW_RASTER)
    if (payloadIndex >= uint(XR_ADV_StereoViewMasks.masks.length()) ||
        (XR_ADV_StereoViewMasks.masks[payloadIndex] & (1u << XR_ADV_RASTER_VIEW_INDEX)) == 0u)
    {
        XR_ADV_RejectVisibilityVertex();
        return;
    }
#endif
    if (payloadIndex >= uint(XR_ADV_VisibilityPayloads.records.length()))
    {
        atomicAdd(XR_ADV_VisibilityCounters.decodeOutOfBounds, 1u);
        XR_ADV_RejectVisibilityVertex();
        return;
    }

    XRAdvancedVisibilityPayload payload =
        XR_ADV_VisibilityPayloads.records[payloadIndex];
    uint producer = payloadIndex <
            uint(XR_ADV_VisibilityProducers.records.length())
        ? XR_ADV_VisibilityProducers.records[payloadIndex]
        : XR_ADV_VIS_INVALID;
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
    XRAdvancedPreparedDrawDeformationRecord preparedDeformation;
    bool deformed = XR_ADV_TryLoadPreparedDrawDeformation(
        drawDense,
        draw,
        preparedDeformation);
    bool producerRequiresDeformation =
        producer == XR_ADV_VIS_PRODUCER_CPU_PRE_SKINNED;
    if (producerRequiresDeformation && !deformed)
    {
        atomicAdd(XR_ADV_VisibilityCounters.decodeOutOfBounds, 1u);
        XR_ADV_RejectVisibilityVertex();
        return;
    }
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
    if (XR_ADV_RASTER_VIEW_INDEX >=
        uint(XR_ADV_Views.records.length()))
    {
        atomicAdd(XR_ADV_VisibilityCounters.decodeOutOfBounds, 1u);
        XR_ADV_RejectVisibilityVertex();
        return;
    }
    XRAdvancedViewRecord viewRecord = XR_ADV_LoadView(
        XR_ADV_RASTER_VIEW_INDEX);

    vec3 localPosition = Position;
    vec2 localTexCoord0 = TexCoord0;
    uint selectedVertex = uint(XR_ADV_VERTEX_INDEX);
    if (producer == XR_ADV_VIS_PRODUCER_INDIRECT_INDEXED)
    {
        uint vertexBase = deformed
            ? preparedDeformation.currentVertexOffset
            : geometryRecord.currentVertexData.elementOffset;
        if (vertexBase > 0xffffffffu - selectedVertex)
        {
            atomicAdd(XR_ADV_VisibilityCounters.decodeOutOfBounds, 1u);
            XR_ADV_RejectVisibilityVertex();
            return;
        }
        selectedVertex += vertexBase;
    }
#if defined(XR_ADV_BACKEND_OPENGL)
    // OpenGL reads static and prepared vertices from the selected arena.
#endif
#if defined(XR_ADV_BACKEND_OPENGL)
    if (true)
#else
    if (producer == XR_ADV_VIS_PRODUCER_INDIRECT_INDEXED ||
        producer == XR_ADV_VIS_PRODUCER_CPU_PRE_SKINNED)
#endif
    {
    if (selectedVertex >= (deformed ? uint(XR_ADV_VisibilityCurrentVertices.records.length()) : uint(XR_ADV_VisibilityStaticVertices.records.length())))
    {
        atomicAdd(XR_ADV_VisibilityCounters.decodeOutOfBounds, 1u);
        XR_ADV_RejectVisibilityVertex();
        return;
    }
    XRAdvancedVisibilityPackedVertex vertex = deformed
        ? XR_ADV_VisibilityCurrentVertices.records[selectedVertex]
        : XR_ADV_VisibilityStaticVertices.records[selectedVertex];
    localPosition = vertex.position;
    localTexCoord0 = unpackHalf2x16(vertex.texCoord0Half);
    }
#if defined(XR_ADV_VIS_VERTEX_DISPLACEMENT)
    localPosition += XR_ADV_ApplyVisibilityVertexDisplacement(
        payload,
        materialDense,
        localPosition,
        localTexCoord0);
#endif

    vec3 previousLocalPosition = localPosition;
    bool previousVertexValid = !deformed;
    if (deformed)
    {
        uint currentVertex = selectedVertex;
        uint currentEnd =
            preparedDeformation.currentVertexOffset +
            preparedDeformation.vertexCount;
        bool currentInRange =
            currentEnd >= preparedDeformation.currentVertexOffset &&
            currentVertex >= preparedDeformation.currentVertexOffset &&
            currentVertex < currentEnd;
        bool usePrevious =
            XR_ADV_PreparedDeformationPreviousValid(
                preparedDeformation);
        uint localVertex = currentInRange
            ? currentVertex -
                preparedDeformation.currentVertexOffset
            : 0u;
        uint previousVertex = usePrevious
            ? preparedDeformation.previousVertexOffset + localVertex
            : preparedDeformation.currentVertexOffset + localVertex;
        bool previousInRange = currentInRange &&
            (usePrevious
                ? previousVertex <
                    uint(XR_ADV_VisibilityPreviousVertices.records.length())
                : previousVertex <
                    uint(XR_ADV_VisibilityCurrentVertices.records.length()));
        if (!currentInRange || !previousInRange)
        {
            atomicAdd(
                XR_ADV_VisibilityCounters.decodeOutOfBounds,
                1u);
            XR_ADV_RejectVisibilityVertex();
            return;
        }
        previousLocalPosition = usePrevious
            ? XR_ADV_VisibilityPreviousVertices.records[
                previousVertex].position
            : XR_ADV_VisibilityCurrentVertices.records[
                previousVertex].position;
        previousVertexValid = usePrevious;
    }

    vec4 worldPosition =
        vec4(localPosition, 1.0) * transformRecord.world;
    vec4 previousWorldPosition =
        vec4(previousLocalPosition, 1.0) *
        previousTransformRecord.world;
    gl_Position =
        worldPosition * viewRecord.viewProjectionJittered;

    vec4 currentUnjitteredClip =
        worldPosition * viewRecord.viewProjectionUnjittered;
    vec4 previousUnjitteredClip =
        previousWorldPosition *
            viewRecord.previousViewProjectionUnjittered;
    VisibilityDrawIndex = payload.draw.index;
    VisibilityProducer = producer;
    VisibilityViewIndex = XR_ADV_RASTER_VIEW_INDEX;
    VisibilityOrigin = (XR_ADV_VisibilityRasterPush.producerAndOrigin >> XR_ADV_VIS_ORIGIN_SHIFT) & 1u;
    VisibilityCoverageUv = localTexCoord0;
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
        previousVertexValid &&
        (viewRecord.flags & XR_ADV_VIEW_TEMPORAL_HISTORY_VALID) != 0u &&
        ((payload.flags >> 16u) & 15u) == 0u &&
        geometrySourcesValid &&
        temporalInputsValid
            ? 1u
            : 0u;

    uint editorDense = XR_ADV_ResolveVisibilityHandle(
        draw.editorIdentity,
        VisibilityEditorIdentityLookupSegment,
        XR_ADV_DIAGNOSTIC_DRAW);
    if (editorDense != XR_ADV_INVALID_DENSE_INDEX)
    {
        XRAdvancedEditorIdentityRecord editorIdentity =
            XR_ADV_LoadEditorIdentity(editorDense);
        VisibilitySelectionId = editorIdentity.selectionId;
        VisibilityEditorFlags = editorIdentity.flags & 3u;
    }
    else
    {
        VisibilitySelectionId = XR_ADV_VIS_INVALID;
        VisibilityEditorFlags = 0u;
    }

    // Eye membership comes from the independently culled submission stream
    // (and the GL multiview payload mask). Object layer/pass masks are not eye
    // indices: treating scene layer zero as view bit zero drops the right eye.
}
