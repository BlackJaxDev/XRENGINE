#version 450

#extension GL_ARB_shader_draw_parameters : require

// Directional shadow lane: the canonical visibility payload identity resolved
// exactly as VisibilityRaster.vert resolves it, projected by one cascade's
// world-to-clip matrix instead of a view record. No jitter, velocity or
// history participates; the atlas tile receives fixed-function depth only.
layout(push_constant, std430) uniform XRAdvancedDirectionalShadowPushConstants
{
    uint meshArgumentBase;
    uint producerAndOrigin;
    uint cascadeIndex;
    uint flags;
    layout(row_major) mat4 viewProjection;
} XR_ADV_DirectionalShadowPush;

// Diagnostic counters stay in the family's first view slot: cascades are not
// canonical views and must never index past the sealed per-view segments.
#define XR_ADV_VISIBILITY_COUNTER_INDEX 0u
#include "VisibilityInterface.glslinc"

#define XR_ADV_VERTEX_INDEX gl_VertexIndex

layout(location = 0) in vec3 Position;
layout(location = 1) in vec2 TexCoord0;

layout(location = 0) out vec2 ShadowCoverageUv;
layout(location = 1) flat out uint ShadowMaterialDenseIndex;
layout(location = 2) out vec2 ShadowCoverageUv1;
layout(location = 3) out vec4 ShadowCoverageColor;
layout(location = 4) out vec3 ShadowCoverageWorldPosition;
layout(location = 5) out vec3 ShadowCoverageLocalPosition;
layout(location = 6) out vec2 ShadowCoverageUv2;
layout(location = 7) out vec2 ShadowCoverageUv3;
layout(location = 8) flat out uint ShadowDrawIndex;
layout(location = 9) flat out uint ShadowProducer;
layout(location = 10) flat out uint ShadowPrimitiveBase;

uint XR_ADV_VisibilityPayloadIndex()
{
    if ((XR_ADV_DirectionalShadowPush.producerAndOrigin &
            XR_ADV_VIS_PRODUCER_MASK) != XR_ADV_VIS_PRODUCER_INDIRECT_INDEXED)
        return gl_BaseInstanceARB;
    uint instanceIndex = uint(gl_InstanceIndex);
    if (gl_BaseInstanceARB >=
            uint(XR_ADV_VisibilityMeshPayloads.records.length()))
        return XR_ADV_VIS_INVALID;
    uint firstPayload =
        XR_ADV_VisibilityMeshPayloads.records[gl_BaseInstanceARB];
    if (firstPayload >= uint(XR_ADV_VisibilityPayloads.records.length()))
        return XR_ADV_VIS_INVALID;
    if (XR_ADV_VisibilityPayloads.records[firstPayload].instanceCount > 1u)
        return firstPayload;
    return instanceIndex < uint(XR_ADV_VisibilityMeshPayloads.records.length())
        ? XR_ADV_VisibilityMeshPayloads.records[instanceIndex]
        : XR_ADV_VIS_INVALID;
}

void XR_ADV_RejectShadowVertex()
{
    gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
    ShadowCoverageUv = vec2(0.0);
    ShadowMaterialDenseIndex = XR_ADV_INVALID_DENSE_INDEX;
    ShadowCoverageUv1 = vec2(0.0);
    ShadowCoverageUv2 = vec2(0.0);
    ShadowCoverageUv3 = vec2(0.0);
    ShadowCoverageColor = vec4(1.0);
    ShadowCoverageWorldPosition = vec3(0.0);
    ShadowCoverageLocalPosition = vec3(0.0);
    ShadowDrawIndex = XR_ADV_VIS_INVALID;
    ShadowProducer = XR_ADV_VIS_INVALID;
    ShadowPrimitiveBase = 0u;
}

void main()
{
    uint payloadIndex = XR_ADV_VisibilityPayloadIndex();
    if (payloadIndex >= uint(XR_ADV_VisibilityPayloads.records.length()))
    {
        XR_ADV_RejectShadowVertex();
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
    uint materialDense = XR_ADV_ResolveVisibilityHandle(
        payload.material,
        VisibilityMaterialLookupSegment,
        XR_ADV_DIAGNOSTIC_MATERIAL);
    if (drawDense == XR_ADV_INVALID_DENSE_INDEX ||
        materialDense == XR_ADV_INVALID_DENSE_INDEX)
    {
        XR_ADV_RejectShadowVertex();
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
        XR_ADV_RejectShadowVertex();
        return;
    }
    uint transformDense = XR_ADV_ResolveVisibilityHandle(
        draw.currentTransform,
        VisibilityTransformLookupSegment,
        XR_ADV_DIAGNOSTIC_DRAW);
    if (transformDense == XR_ADV_INVALID_DENSE_INDEX)
    {
        XR_ADV_RejectShadowVertex();
        return;
    }

    XRAdvancedTransformRecord transformRecord =
        XR_ADV_LoadTransform(transformDense);

    vec3 localPosition = Position;
    vec2 localTexCoord0 = TexCoord0;
    vec2 localTexCoord1 = localTexCoord0;
    vec2 localTexCoord2 = vec2(0.0);
    vec2 localTexCoord3 = vec2(0.0);
    vec4 localColor = vec4(1.0);
    uint selectedVertex = uint(XR_ADV_VERTEX_INDEX);
    if (producer == XR_ADV_VIS_PRODUCER_INDIRECT_INDEXED)
    {
        uint geometryDense = XR_ADV_ResolveVisibilityHandle(
            payload.geometry,
            VisibilityGeometryLookupSegment,
            XR_ADV_DIAGNOSTIC_MESH);
        if (geometryDense == XR_ADV_INVALID_DENSE_INDEX)
        {
            XR_ADV_RejectShadowVertex();
            return;
        }
        uint vertexBase = deformed
            ? preparedDeformation.currentVertexOffset
            : XR_ADV_LoadGeometry(geometryDense).currentVertexData.elementOffset;
        if (vertexBase > 0xffffffffu - selectedVertex)
        {
            XR_ADV_RejectShadowVertex();
            return;
        }
        selectedVertex += vertexBase;
        if (selectedVertex >= (deformed
            ? uint(XR_ADV_VisibilityCurrentVertices.records.length())
            : uint(XR_ADV_VisibilityStaticVertices.records.length())))
        {
            XR_ADV_RejectShadowVertex();
            return;
        }
        XRAdvancedVisibilityPackedVertex vertex = deformed
            ? XR_ADV_VisibilityCurrentVertices.records[selectedVertex]
            : XR_ADV_VisibilityStaticVertices.records[selectedVertex];
        localPosition = vertex.position;
        localTexCoord0 = unpackHalf2x16(vertex.texCoord0Half);
        localTexCoord1 = (vertex.flags & XR_ADV_VERTEX_HAS_TEXCOORD1) != 0u
            ? unpackHalf2x16(vertex.texCoord1Half)
            : localTexCoord0;
        localTexCoord2 = (vertex.flags & XR_ADV_VERTEX_HAS_TEXCOORD2) != 0u
            ? unpackHalf2x16(vertex.texCoord2Half)
            : vec2(0.0);
        localTexCoord3 = (vertex.flags & XR_ADV_VERTEX_HAS_TEXCOORD3) != 0u
            ? unpackHalf2x16(vertex.texCoord3Half)
            : vec2(0.0);
        localColor = unpackUnorm4x8(vertex.color0Rgba8);
    }
#if defined(XR_ADV_VIS_VERTEX_DISPLACEMENT)
    localPosition += XR_ADV_ApplyVisibilityVertexDisplacement(
        payload,
        materialDense,
        localPosition,
        localTexCoord0);
#endif

    if (deformed)
    {
        // Deformed vertices are fetched through the same packed arena offsets
        // as the visibility raster; reject vertices outside the prepared range.
        uint currentVertex = selectedVertex;
        uint currentEnd =
            preparedDeformation.currentVertexOffset +
            preparedDeformation.vertexCount;
        bool currentInRange =
            currentEnd >= preparedDeformation.currentVertexOffset &&
            currentVertex >= preparedDeformation.currentVertexOffset &&
            currentVertex < currentEnd;
        if (!currentInRange)
        {
            XR_ADV_RejectShadowVertex();
            return;
        }
    }

    vec4 worldPosition =
        vec4(localPosition, 1.0) * transformRecord.world;
    gl_Position =
        worldPosition * XR_ADV_DirectionalShadowPush.viewProjection;
    ShadowCoverageUv = localTexCoord0;
    ShadowCoverageUv1 = localTexCoord1;
    ShadowCoverageUv2 = localTexCoord2;
    ShadowCoverageUv3 = localTexCoord3;
    ShadowCoverageColor = localColor;
    ShadowCoverageWorldPosition = worldPosition.xyz;
    ShadowCoverageLocalPosition = localPosition;
    ShadowMaterialDenseIndex = materialDense;
    ShadowDrawIndex = payload.draw.index;
    ShadowProducer = producer;
    ShadowPrimitiveBase = payload.firstIndex / 3u;
}
