float3x3 NormalMatrix(float3x3 m)
{
    float3 row0 = cross(m[1], m[2]);
    float3 row1 = cross(m[2], m[0]);
    float3 row2 = cross(m[0], m[1]);
    return float3x3(row0, row1, row2) / dot(m[0], row0);
}
// Perspective-correct reconstruction port of Advanced/Reconstruction/ReconstructSurface.
uint XR_ADV_ReconstructionTriangleByte(uint index) { uint word = GeometryArena[GeometryRow(6u, index >> 2u, 1u)]; return (word >> ((index & 3u) * 8u)) & 255u; }
#define XRAdvancedPackedVertex XRAdvancedVisibilityPackedVertex
#define XRAdvancedReconstructionMeshlet XRAdvancedVisibilityMeshletDescriptor
float3 XR_ADV_DecodeOct(uint packedValue)
{
    float2 value = float2(
        float(int(packedValue << 16u) >> 16u) / 32767.0,
        float(int(packedValue) >> 16u) / 32767.0);
    float3 decoded =
        float3(value, 1.0 - abs(value.x) - abs(value.y));
    if (decoded.z < 0.0)
    {
        float2 folded =
            (1.0 - abs(decoded.yx)) *
            lerp(float2(-1.0), float2(1.0),
                float2(greaterThanEqual(decoded.xy, float2(0.0))));
        decoded.xy = folded;
    }
    return normalize(decoded);
}

float3 XR_ADV_DecodeTangent(
    uint packedValue,
    out float handedness)
{
    handedness = (packedValue & 0x80000000u) != 0u
        ? -1.0
        : 1.0;
    int signedY = int((packedValue >> 16u) & 0x7FFFu);
    if ((signedY & 0x4000) != 0)
        signedY |= int(0xFFFF8000u);
    float x = float(int(packedValue << 16u) >> 16u) / 32767.0;
    float y = float(signedY) / 16383.0;
    float3 decoded = float3(x, y, 1.0 - abs(x) - abs(y));
    if (decoded.z < 0.0)
    {
        float oldX = decoded.x;
        decoded.x =
            (1.0 - abs(decoded.y)) *
            (oldX >= 0.0 ? 1.0 : -1.0);
        decoded.y =
            (1.0 - abs(oldX)) *
            (decoded.y >= 0.0 ? 1.0 : -1.0);
    }
    return normalize(decoded);
}

float4 XR_ADV_UnpackRgba8(uint packedValue)
{
    return float4(
        float(packedValue & 0xFFu),
        float((packedValue >> 8u) & 0xFFu),
        float((packedValue >> 16u) & 0xFFu),
        float((packedValue >> 24u) & 0xFFu)) / 255.0;
}

bool XR_ADV_IsFinite(float2 value)
{
    // IEEE exponent classification avoids loop-based vector isnan/isinf lowering
    // in cooked WGSL while retaining finite, infinity and NaN behavior exactly.
    return all((asuint(value) & 0x7f800000u) != 0x7f800000u);
}

bool XR_ADV_IsFinite(float3 value)
{
    return all((asuint(value) & 0x7f800000u) != 0x7f800000u);
}

bool XR_ADV_IsFinite(float4 value)
{
    return all((asuint(value) & 0x7f800000u) != 0x7f800000u);
}

#define XR_ADV_RECONSTRUCTION_MIN_W 1.0e-6
#define XR_ADV_RECONSTRUCTION_MIN_AREA 1.0e-6
#define XR_ADV_GEOMETRY_SOURCE_PRESKINNED 1u
#define XR_ADV_GEOMETRY_MISSING_USE_FALLBACK 1u

// Match the CPU tangent-frame reference for absent or cancelling attributes.
// Never normalize a zero/invalid vector into the native lighting inputs.
float3 XR_ADV_NormalizeOr(float3 value, float3 fallback)
{
    float lengthSquared = dot(value, value);
    return isnan(lengthSquared) || isinf(lengthSquared) || lengthSquared <= 1.0e-20
        ? fallback
        : value * rsqrt(lengthSquared);
}

float3 XR_ADV_FallbackTangent(float3 normal)
{
    float3 axis = abs(normal.z) < 0.999
        ? float3(0.0, 0.0, 1.0)
        : float3(0.0, 1.0, 0.0);
    return normalize(cross(axis, normal));
}

float3 XR_ADV_OrthogonalTangent(float3 normal, float3 sourceTangent)
{
    // Subtracting the normal projection can leave a tiny parallel residual.
    // Normalizing that residual makes N x T zero and the bitangent nonfinite.
    // Build the orthogonal direction from crosses, rejecting near-parallel
    // authored tangents relative to their original magnitude.
    float3 perpendicular = cross(normal, sourceTangent);
    float perpendicularLengthSquared = dot(perpendicular, perpendicular);
    float tangentLengthSquared = dot(sourceTangent, sourceTangent);
    if (isnan(tangentLengthSquared) || isinf(tangentLengthSquared) ||
        isnan(perpendicularLengthSquared) || isinf(perpendicularLengthSquared) ||
        perpendicularLengthSquared <= max(1.0e-20, tangentLengthSquared * 1.0e-12))
        return XR_ADV_FallbackTangent(normal);
    perpendicular *= rsqrt(perpendicularLengthSquared);
    return XR_ADV_NormalizeOr(cross(perpendicular, normal), XR_ADV_FallbackTangent(normal));
}

float2 XR_ADV_ClipToPixel(
    float4 clip,
    XRAdvancedViewRecord viewRecord)
{
    return XR_ADV_ClipNdcToFramebufferUv(
        viewRecord,
        clip.xy / clip.w) *
        viewRecord.renderSizeAndInverse.xy;
}

bool XR_ADV_TryPerspectiveBarycentrics(
    float2 pixelCenter,
    float4 clip0,
    float4 clip1,
    float4 clip2,
    XRAdvancedViewRecord viewRecord,
    out float3 weights,
    out float3 weightDx,
    out float3 weightDy)
{
    weights = float3(0.0);
    weightDx = float3(0.0);
    weightDy = float3(0.0);
    if (!XR_ADV_IsFinite(clip0) ||
        !XR_ADV_IsFinite(clip1) ||
        !XR_ADV_IsFinite(clip2) ||
        abs(clip0.w) <= XR_ADV_RECONSTRUCTION_MIN_W ||
        abs(clip1.w) <= XR_ADV_RECONSTRUCTION_MIN_W ||
        abs(clip2.w) <= XR_ADV_RECONSTRUCTION_MIN_W)
    {
        return false;
    }

    float2 p0 = XR_ADV_ClipToPixel(clip0, viewRecord);
    float2 p1 = XR_ADV_ClipToPixel(clip1, viewRecord);
    float2 p2 = XR_ADV_ClipToPixel(clip2, viewRecord);
    float denominator =
        (p1.y - p2.y) * (p0.x - p2.x) +
        (p2.x - p1.x) * (p0.y - p2.y);
    if (isnan(denominator) ||
        isinf(denominator) ||
        abs(denominator) <= XR_ADV_RECONSTRUCTION_MIN_AREA)
    {
        return false;
    }

    float3 linear;
    linear.x =
        ((p1.y - p2.y) * (pixelCenter.x - p2.x) +
         (p2.x - p1.x) * (pixelCenter.y - p2.y)) /
        denominator;
    linear.y =
        ((p2.y - p0.y) * (pixelCenter.x - p2.x) +
         (p0.x - p2.x) * (pixelCenter.y - p2.y)) /
        denominator;
    linear.z = 1.0 - linear.x - linear.y;
    float3 linearDx = float3(
        p1.y - p2.y,
        p2.y - p0.y,
        p0.y - p1.y) / denominator;
    float3 linearDy = float3(
        p2.x - p1.x,
        p0.x - p2.x,
        p1.x - p0.x) / denominator;
    float3 inverseW =
        1.0 / float3(clip0.w, clip1.w, clip2.w);
    float3 numerator = linear * inverseW;
    float3 numeratorDx = linearDx * inverseW;
    float3 numeratorDy = linearDy * inverseW;
    float sum = numerator.x + numerator.y + numerator.z;
    if (isnan(sum) ||
        isinf(sum) ||
        abs(sum) <= XR_ADV_RECONSTRUCTION_MIN_W)
    {
        return false;
    }

    float sumDx =
        numeratorDx.x + numeratorDx.y + numeratorDx.z;
    float sumDy =
        numeratorDy.x + numeratorDy.y + numeratorDy.z;
    float inverseSum = 1.0 / sum;
    weights = numerator * inverseSum;
    weightDx =
        (numeratorDx * sum - numerator * sumDx) *
        (inverseSum * inverseSum);
    weightDy =
        (numeratorDy * sum - numerator * sumDy) *
        (inverseSum * inverseSum);
    return XR_ADV_IsFinite(weights) &&
        XR_ADV_IsFinite(weightDx) &&
        XR_ADV_IsFinite(weightDy);
}

float2 XR_ADV_Interpolate(
    float2 value0,
    float2 value1,
    float2 value2,
    float3 weights,
    bool flatQualified)
{
    return flatQualified
        ? value0
        : value0 * weights.x +
          value1 * weights.y +
          value2 * weights.z;
}

float3 XR_ADV_Interpolate(
    float3 value0,
    float3 value1,
    float3 value2,
    float3 weights,
    bool flatQualified)
{
    return flatQualified
        ? value0
        : value0 * weights.x +
          value1 * weights.y +
          value2 * weights.z;
}

float4 XR_ADV_Interpolate(
    float4 value0,
    float4 value1,
    float4 value2,
    float3 weights,
    bool flatQualified)
{
    return flatQualified
        ? value0
        : value0 * weights.x +
          value1 * weights.y +
          value2 * weights.z;
}

float2 XR_ADV_Gradient(
    float2 value0,
    float2 value1,
    float2 value2,
    float3 weightDerivative)
{
    return value0 * weightDerivative.x +
        value1 * weightDerivative.y +
        value2 * weightDerivative.z;
}

bool XR_ADV_TryPrimitiveVertices(
    XRAdvancedGeometryRecord geometry,
    uint producer,
    uint encodedPrimitive,
    out uint3 vertices)
{
    vertices = uint3(0u);
    bool meshlet =
        producer == XR_ADV_VIS_PRODUCER_STATIC_MESHLET ||
        producer == XR_ADV_VIS_PRODUCER_SKINNED_MESHLET;
    if (!meshlet)
    {
        if (encodedPrimitive > 0x55555555u)
            return false;
        uint firstIndex = encodedPrimitive * 3u;
        uint geometryEnd =
            geometry.indexData.elementOffset +
            geometry.indexData.elementCount;
        if (geometryEnd < geometry.indexData.elementOffset ||
            firstIndex < geometry.indexData.elementOffset ||
            firstIndex > geometryEnd ||
            geometryEnd - firstIndex < 3u ||
            firstIndex + 2u >=
                GeometryCount(1u, 4u))
        {
            return false;
        }
        vertices = uint3(
            GeometryArena[GeometryRow(1u, firstIndex, 1u)],
            GeometryArena[GeometryRow(1u, firstIndex + 1u, 1u)],
            GeometryArena[GeometryRow(1u, firstIndex + 2u, 1u)]);
        return true;
    }

    // Meshlet primitive identity is unchanged by compute triangle expansion.
    uint meshletIndex = encodedPrimitive >> 8u;
    uint localPrimitive = encodedPrimitive & 0xFFu;
    if (meshletIndex < geometry.meshletFirst ||
        meshletIndex - geometry.meshletFirst >= geometry.meshletCount ||
        meshletIndex >=
            GeometryCount(4u, 80u))
    {
        return false;
    }

    XRAdvancedReconstructionMeshlet descriptor =
        ReadXRAdvancedVisibilityMeshletDescriptor(GeometryRow(4u, meshletIndex, 20u));
    if (localPrimitive >= descriptor.triangleCount)
        return false;
    uint remapBase = geometry.meshletVertexIndices.elementOffset;
    uint remapLength = geometry.meshletVertexIndices.elementCount;
    uint wordBase = geometry.meshletTriangleWords.elementOffset;
    uint wordLength = geometry.meshletTriangleWords.elementCount;
    uint remapCount = GeometryCount(5u, 4u);
    uint wordCount = GeometryCount(6u, 4u);
    if (remapBase > remapCount || remapLength > remapCount - remapBase ||
        wordBase > wordCount || wordLength > wordCount - wordBase ||
        wordBase > 0x3fffffffu || wordLength > 0x3fffffffu ||
        descriptor.vertexOffset > remapLength || descriptor.vertexCount > remapLength - descriptor.vertexOffset ||
        descriptor.triangleByteOffset > wordLength * 4u ||
        descriptor.triangleCount > (wordLength * 4u - descriptor.triangleByteOffset) / 3u)
        return false;
    uint triangleByte =
        wordBase * 4u + descriptor.triangleByteOffset + localPrimitive * 3u;
    uint triangleByteCount = GeometryCount(6u, 1u);
    if (triangleByte < descriptor.triangleByteOffset ||
        triangleByte > triangleByteCount || triangleByteCount - triangleByte < 3u)
        return false;
    uint3 localVertices = uint3(
        XR_ADV_ReconstructionTriangleByte(triangleByte),
        XR_ADV_ReconstructionTriangleByte(triangleByte + 1u),
        XR_ADV_ReconstructionTriangleByte(triangleByte + 2u));
    if (any(greaterThanEqual(
            localVertices,
            uint3(descriptor.vertexCount))))
    {
        return false;
    }

    uint3 remapIndices =
        uint3(remapBase + descriptor.vertexOffset) + localVertices;
    if (any(lessThan(remapIndices, uint3(remapBase))) ||
        any(greaterThanEqual(remapIndices, uint3(remapCount))))
        return false;
    vertices = uint3(
        GeometryArena[GeometryRow(5u, remapIndices.x, 1u)],
        GeometryArena[GeometryRow(5u, remapIndices.y, 1u)],
        GeometryArena[GeometryRow(5u, remapIndices.z, 1u)]);
    return true;
}

bool XR_ADV_TryLoadVertex(
    bool deformed,
    bool previous,
    uint index,
    out XRAdvancedPackedVertex vertex)
{
    vertex = XRAdvancedPackedVertex(
        float3(0.0),
        0u, 0u, 0u, 0u, 0u, 0u, 0u,
        0u, 0u, 0u, 0u, 0u, 0u);
    if (!deformed)
    {
        if (index >= GeometryCount(0u, 64u))
            return false;
        vertex = ReadXRAdvancedVisibilityPackedVertex(GeometryRow(0u, index, 16u));
        return true;
    }
    if (previous)
    {
        if (index >=
            GeometryCount(3u, 64u))
        {
            return false;
        }
        vertex =
            ReadXRAdvancedVisibilityPackedVertex(GeometryRow(3u, index, 16u));
        return true;
    }
    if (index >=
        GeometryCount(2u, 64u))
    {
        return false;
    }
    vertex = ReadXRAdvancedVisibilityPackedVertex(GeometryRow(2u, index, 16u));
    return true;
}

float3 XR_ADV_ReconstructWorldPositionFromDepth(
    float2 pixelCenter,
    float depth,
    XRAdvancedViewRecord viewRecord)
{
    float2 framebufferUv =
        pixelCenter * viewRecord.renderSizeAndInverse.zw;
    float2 ndc = XR_ADV_FramebufferUvToClipNdc(
        viewRecord,
        framebufferUv);
    // The inverse matrix carries the frozen view's depth convention. OpenGL
    // also renders zero-to-one projections through clip control.
    float clipDepth = (viewRecord.flags & XR_ADV_VIEW_DEPTH_ZERO_TO_ONE) != 0u
        ? depth
        : depth * 2.0 - 1.0;
    float4 world =
        mul(float4(ndc, clipDepth, 1.0), viewRecord.inverseViewProjectionJittered);
    return world.xyz / world.w;
}

bool XR_ADV_TryReconstructSurface(
    uint2 identity,
    uint metadata,
    float depth,
    float2 pixelCenter,
    out XRAdvancedSurface surface)
{
    surface = XR_ADV_InvalidSurface(
        identity,
        XR_ADV_RECONSTRUCTION_BACKGROUND);
    uint payloadVersion =
        (metadata >> XR_ADV_VIS_VERSION_SHIFT) & 0xFFu;
    if (identity.x == 0u ||
        identity.x == XR_ADV_VIS_INVALID ||
        identity.y == XR_ADV_VIS_INVALID ||
        metadata == XR_ADV_VIS_INVALID)
    {
        return false;
    }
    if (payloadVersion != XR_ADV_VISIBILITY_PAYLOAD_VERSION)
    {
        surface.invalidReason =
            XR_ADV_RECONSTRUCTION_PAYLOAD_VERSION;
        return false;
    }

    uint producer = metadata & XR_ADV_VIS_PRODUCER_MASK;
    uint viewIndex =
        (metadata >> XR_ADV_VIS_VIEW_SHIFT) & 0xFFu;
    if (!XR_ADV_IsSupportedVisibilityProducer(producer))
        return false;
    uint drawDense = XR_ADV_ResolveVisibilityStableIndex(
        identity.x,
        VisibilityDrawLookupSegment,
        XR_ADV_DIAGNOSTIC_DRAW);
    if (drawDense == XR_ADV_INVALID_DENSE_INDEX ||
        drawDense >= SceneCount(0u))
    {
        surface.invalidReason =
            XR_ADV_RECONSTRUCTION_DRAW_NOT_RESIDENT;
        return false;
    }

    XRAdvancedDrawRecord draw = XR_ADV_LoadDraw(drawDense);
    uint instanceDense = XR_ADV_ResolveVisibilityHandle(
        draw.instance,
        VisibilityInstanceLookupSegment,
        XR_ADV_DIAGNOSTIC_INSTANCE);
    if (instanceDense == XR_ADV_INVALID_DENSE_INDEX ||
        instanceDense >= SceneCount(1u))
    {
        surface.invalidReason = XR_ADV_RECONSTRUCTION_STALE_GENERATION;
        return false;
    }
    XRAdvancedInstanceRecord instance = XR_ADV_LoadInstance(instanceDense);
    XRAdvancedPreparedDrawDeformationRecord preparedTemporalState;
    bool hasPreparedTemporalState =
        XR_ADV_TryLoadPreparedDrawTemporalState(
            drawDense,
            draw,
            preparedTemporalState);
    // The sidecar is the only current-frame temporal authority. A stale or
    // absent relation fails closed without making static surface shading fail.
    uint velocityValidityReason = hasPreparedTemporalState
        ? XR_ADV_PreparedDeformationTemporalReason(preparedTemporalState)
        : 7u; // EAdvancedVelocityValidityReason.HistoryReset
    uint geometryDense = XR_ADV_ResolveVisibilityHandle(
        draw.geometry,
        VisibilityGeometryLookupSegment,
        XR_ADV_DIAGNOSTIC_MESH);
    if (geometryDense == XR_ADV_INVALID_DENSE_INDEX)
    {
        surface.invalidReason =
            XR_ADV_RECONSTRUCTION_GEOMETRY_MISSING;
        return false;
    }
    XRAdvancedGeometryRecord geometry =
        XR_ADV_LoadGeometry(geometryDense);
    if (!XR_ADV_GeometryIsResident(geometry) &&
        geometry.missingBehavior ==
            XR_ADV_GEOMETRY_MISSING_USE_FALLBACK &&
        geometry.fallbackGeometry.index != draw.geometry.index)
    {
        uint fallbackDense = XR_ADV_ResolveVisibilityHandle(
            geometry.fallbackGeometry,
            VisibilityGeometryLookupSegment,
            XR_ADV_DIAGNOSTIC_MESH);
        if (fallbackDense != XR_ADV_INVALID_DENSE_INDEX)
        {
            geometry = XR_ADV_LoadGeometry(fallbackDense);
            geometryDense = fallbackDense;
        }
    }
    if (!XR_ADV_GeometryIsResident(geometry))
    {
        surface.invalidReason =
            XR_ADV_RECONSTRUCTION_GEOMETRY_NOT_RESIDENT;
        return false;
    }

    uint materialDense = XR_ADV_ResolveVisibilityHandle(
        draw.material,
        VisibilityMaterialLookupSegment,
        XR_ADV_DIAGNOSTIC_MATERIAL);
    if (materialDense == XR_ADV_INVALID_DENSE_INDEX)
    {
        surface.invalidReason =
            XR_ADV_RECONSTRUCTION_MATERIAL_NOT_RESIDENT;
        return false;
    }
    XRAdvancedMaterialRecord material =
        XR_ADV_LoadMaterial(materialDense);
    XRAdvancedGpuHandle kernelHandle = XRAdvancedGpuHandle(
        material.shadingKernelId,
        material.shadingKernelGeneration);
    uint kernelDense = XR_ADV_ResolveVisibilityHandle(
        kernelHandle,
        VisibilityShadingKernelLookupSegment,
        XR_ADV_DIAGNOSTIC_MATERIAL);
    if (kernelDense == XR_ADV_INVALID_DENSE_INDEX)
    {
        surface.invalidReason =
            XR_ADV_RECONSTRUCTION_KERNEL_NOT_RESIDENT;
        return false;
    }
    if (viewIndex >= Parameters.viewCount)
    {
        surface.invalidReason =
            XR_ADV_RECONSTRUCTION_VIEW_OUT_OF_RANGE;
        return false;
    }

    uint currentTransformDense = XR_ADV_ResolveVisibilityHandle(
        draw.currentTransform,
        VisibilityTransformLookupSegment,
        XR_ADV_DIAGNOSTIC_DRAW);
    uint previousTransformDense = XR_ADV_ResolveVisibilityHandle(
        draw.previousTransform,
        VisibilityTransformLookupSegment,
        XR_ADV_DIAGNOSTIC_DRAW);
    if (currentTransformDense == XR_ADV_INVALID_DENSE_INDEX ||
        previousTransformDense == XR_ADV_INVALID_DENSE_INDEX)
    {
        surface.invalidReason =
            XR_ADV_RECONSTRUCTION_STALE_GENERATION;
        return false;
    }

    uint3 sourceVertices;
    if (!XR_ADV_TryPrimitiveVertices(
            geometry,
            producer,
            identity.y,
            sourceVertices))
    {
        surface.invalidReason =
            XR_ADV_RECONSTRUCTION_PRIMITIVE_OUT_OF_RANGE;
        return false;
    }

    XRAdvancedPreparedDrawDeformationRecord preparedDeformation;
    bool hasPreparedDeformation =
        XR_ADV_TryLoadPreparedDrawDeformation(
            drawDense,
            draw,
            preparedDeformation);
    bool producerRequiresDeformation =
        producer == XR_ADV_VIS_PRODUCER_SKINNED_MESHLET ||
        producer == XR_ADV_VIS_PRODUCER_CPU_PRE_SKINNED;
    if (producerRequiresDeformation && !hasPreparedDeformation)
    {
        surface.invalidReason =
            XR_ADV_RECONSTRUCTION_CURRENT_GEOMETRY_MISSING;
        return false;
    }

    // The overlay is authoritative even for the ordinary indirect-indexed
    // producer. Canonical geometry remains immutable bind-pose topology.
    bool deformed = hasPreparedDeformation;
    if (any(greaterThanEqual(
            sourceVertices,
            uint3(deformed ? preparedDeformation.vertexCount : geometry.vertexCount))))
    {
        surface.invalidReason =
            XR_ADV_RECONSTRUCTION_PRIMITIVE_OUT_OF_RANGE;
        return false;
    }
    uint currentVertexBase = deformed
        ? preparedDeformation.currentVertexOffset
        : geometry.currentVertexData.elementOffset;
    bool previousUsesPreviousBuffer = deformed &&
        XR_ADV_PreparedDeformationPreviousValid(preparedDeformation);
    uint previousVertexBase = deformed
        ? (previousUsesPreviousBuffer
            ? preparedDeformation.previousVertexOffset
            : preparedDeformation.currentVertexOffset)
        : geometry.currentVertexData.elementOffset;
    uint3 currentIndices =
        uint3(currentVertexBase) + sourceVertices;
    uint3 previousIndices =
        uint3(previousVertexBase) + sourceVertices;
    if (any(lessThan(currentIndices, uint3(currentVertexBase))))
    {
        surface.invalidReason = XR_ADV_RECONSTRUCTION_PRIMITIVE_OUT_OF_RANGE;
        return false;
    }
    XRAdvancedPackedVertex current0;
    XRAdvancedPackedVertex current1;
    XRAdvancedPackedVertex current2;
    if (!XR_ADV_TryLoadVertex(
            deformed, false, currentIndices.x, current0) ||
        !XR_ADV_TryLoadVertex(
            deformed, false, currentIndices.y, current1) ||
        !XR_ADV_TryLoadVertex(
            deformed, false, currentIndices.z, current2))
    {
        surface.invalidReason =
            XR_ADV_RECONSTRUCTION_CURRENT_GEOMETRY_MISSING;
        return false;
    }

    XRAdvancedPackedVertex previous0;
    XRAdvancedPackedVertex previous1;
    XRAdvancedPackedVertex previous2;
    bool previousGeometryValid =
        !any(lessThan(previousIndices, uint3(previousVertexBase))) &&
        XR_ADV_TryLoadVertex(
            deformed,
            previousUsesPreviousBuffer,
            previousIndices.x,
            previous0) &&
        XR_ADV_TryLoadVertex(
            deformed,
            previousUsesPreviousBuffer,
            previousIndices.y,
            previous1) &&
        XR_ADV_TryLoadVertex(
            deformed,
            previousUsesPreviousBuffer,
            previousIndices.z,
            previous2);
    XRAdvancedTransformRecord currentTransform =
        XR_ADV_LoadTransform(currentTransformDense);
    XRAdvancedTransformRecord previousTransform =
        XR_ADV_LoadTransform(previousTransformDense);
    XRAdvancedViewRecord viewRecord =
        XR_ADV_LoadView(viewIndex);

    float3 currentWorld0 =
        XR_ADV_TransformPositionRowVector(
            current0.position,
            currentTransform.world);
    float3 currentWorld1 =
        XR_ADV_TransformPositionRowVector(
            current1.position,
            currentTransform.world);
    float3 currentWorld2 =
        XR_ADV_TransformPositionRowVector(
            current2.position,
            currentTransform.world);
    float4 currentClip0 =
        XR_ADV_CurrentJitteredClip(viewRecord, currentWorld0);
    float4 currentClip1 =
        XR_ADV_CurrentJitteredClip(viewRecord, currentWorld1);
    float4 currentClip2 =
        XR_ADV_CurrentJitteredClip(viewRecord, currentWorld2);
    float3 weights;
    float3 weightDx;
    float3 weightDy;
    if (!XR_ADV_TryPerspectiveBarycentrics(
            pixelCenter,
            currentClip0,
            currentClip1,
            currentClip2,
            viewRecord,
            weights,
            weightDx,
            weightDy))
    {
        surface.invalidReason =
            XR_ADV_RECONSTRUCTION_DEGENERATE_TRIANGLE;
        return false;
    }

    uint requiredMask =
        material.requiredAttributeMask &
        XR_ADV_LoadShadingKernel(kernelDense).requiredAttributeMask &
        XR_ADV_REQUIRED_ATTRIBUTE_MASK;
    bool requiresTexCoord1 =
        (requiredMask & XR_ADV_ATTRIBUTE_TEXCOORD1) != 0u;
    bool hasTexCoord1 =
        (current0.flags & XR_ADV_VERTEX_HAS_TEXCOORD1) != 0u &&
        (current1.flags & XR_ADV_VERTEX_HAS_TEXCOORD1) != 0u &&
        (current2.flags & XR_ADV_VERTEX_HAS_TEXCOORD1) != 0u;
    bool flatQualified =
        (requiredMask & XR_ADV_ATTRIBUTE_FLAT) != 0u;
    float3 geometricNormal =
        cross(
            currentWorld1 - currentWorld0,
            currentWorld2 - currentWorld0);
    float geometricLength = length(geometricNormal);
    if (geometricLength <= XR_ADV_RECONSTRUCTION_MIN_AREA ||
        isnan(geometricLength) ||
        isinf(geometricLength))
    {
        surface.invalidReason =
            XR_ADV_RECONSTRUCTION_DEGENERATE_TRIANGLE;
        return false;
    }
    geometricNormal /= geometricLength;

    bool requiresTangent =
        (requiredMask & XR_ADV_ATTRIBUTE_TANGENT) != 0u;
    float3x3 world3 = (float3x3)currentTransform.world;
    bool mirrored = determinant(world3) < 0.0;
    uint engineSurfaceWord, engineSurfaceKind, engineSurfaceRoles;
    bool engineTextureSurface = XR_ADV_TryLoadEngineSurfaceHeader(material,
        engineSurfaceWord, engineSurfaceKind, engineSurfaceRoles) &&
        engineSurfaceKind != XR_ADV_ENGINE_SURFACE_COLOR;
    float3 shadingNormal = geometricNormal;
    if ((requiredMask & XR_ADV_ATTRIBUTE_NORMAL) != 0u)
    {
        float3x3 normalMatrix =
            NormalMatrix(world3);
        if (engineTextureSurface)
        {
            // The engine texture vertex program normalizes each transformed
            // normal before perspective interpolation.
            shadingNormal = XR_ADV_Interpolate(
                XR_ADV_NormalizeOr(mul(XR_ADV_DecodeOct(current0.normalOct), normalMatrix), float3(0.0)),
                XR_ADV_NormalizeOr(mul(XR_ADV_DecodeOct(current1.normalOct), normalMatrix), float3(0.0)),
                XR_ADV_NormalizeOr(mul(XR_ADV_DecodeOct(current2.normalOct), normalMatrix), float3(0.0)),
                weights, flatQualified);
        }
        else
        {
            float3 localNormal = XR_ADV_Interpolate(
                XR_ADV_DecodeOct(current0.normalOct),
                XR_ADV_DecodeOct(current1.normalOct),
                XR_ADV_DecodeOct(current2.normalOct),
                weights,
                flatQualified);
            shadingNormal = XR_ADV_NormalizeOr(mul(localNormal, normalMatrix), geometricNormal);
        }
    }
    float3 tangent;
    float3 bitangent;
    float handedness = 1.0;
    if (requiresTangent)
    {
        float tangentSign0;
        float tangentSign1;
        float tangentSign2;
        float3 localTangent0 =
            XR_ADV_DecodeTangent(
                current0.tangentOctAndSign,
                tangentSign0);
        float3 localTangent1 =
            XR_ADV_DecodeTangent(
                current1.tangentOctAndSign,
                tangentSign1);
        float3 localTangent2 =
            XR_ADV_DecodeTangent(
                current2.tangentOctAndSign,
                tangentSign2);
        float sourceHandedness = flatQualified
            ? tangentSign0
            : dot(
                float3(tangentSign0, tangentSign1, tangentSign2),
                weights) < 0.0
                ? -1.0
                : 1.0;
        if (engineTextureSurface)
        {
            // Match StandardLitColor's texture-normal vertex program: transform
            // both authored tangent and bitangent with the normal matrix, then
            // interpolate the independently normalized vertex basis. Keep a
            // cancelling basis at zero for the material's normal-map guard.
            float3x3 normalMatrix = NormalMatrix(world3);
            float3 tangent0 = XR_ADV_NormalizeOr(mul(localTangent0, normalMatrix), float3(0.0));
            float3 tangent1 = XR_ADV_NormalizeOr(mul(localTangent1, normalMatrix), float3(0.0));
            float3 tangent2 = XR_ADV_NormalizeOr(mul(localTangent2, normalMatrix), float3(0.0));
            float3 bitangent0 = XR_ADV_NormalizeOr(mul(
                cross(XR_ADV_DecodeOct(current0.normalOct), localTangent0) * tangentSign0,
                normalMatrix), float3(0.0));
            float3 bitangent1 = XR_ADV_NormalizeOr(mul(
                cross(XR_ADV_DecodeOct(current1.normalOct), localTangent1) * tangentSign1,
                normalMatrix), float3(0.0));
            float3 bitangent2 = XR_ADV_NormalizeOr(mul(
                cross(XR_ADV_DecodeOct(current2.normalOct), localTangent2) * tangentSign2,
                normalMatrix), float3(0.0));
            tangent = XR_ADV_Interpolate(tangent0, tangent1, tangent2, weights, flatQualified);
            bitangent = XR_ADV_Interpolate(bitangent0, bitangent1, bitangent2, weights, flatQualified);
            handedness = sourceHandedness;
        }
        else
        {
            tangent = mul(XR_ADV_Interpolate(
                localTangent0,
                localTangent1,
                localTangent2,
                weights,
                flatQualified), world3);
            tangent = XR_ADV_OrthogonalTangent(shadingNormal, tangent);
            handedness = sourceHandedness * (mirrored ? -1.0 : 1.0);
            bitangent = normalize(cross(shadingNormal, tangent)) * handedness;
        }
    }
    else
    {
        if (engineTextureSurface)
        {
            tangent = float3(0.0);
            bitangent = float3(0.0);
        }
        else
        {
            tangent = XR_ADV_FallbackTangent(shadingNormal);
            bitangent = normalize(cross(shadingNormal, tangent));
        }
    }

    bool derivativesRequested =
        (requiredMask &
         XR_ADV_ATTRIBUTE_ANALYTICAL_DERIVATIVES) != 0u;
    bool texCoord0Requested =
        (requiredMask & XR_ADV_ATTRIBUTE_TEXCOORD0) != 0u ||
        derivativesRequested;
    float2 uv0 = texCoord0Requested
        ? XR_ADV_Interpolate(
            unpackHalf(current0.texCoord0Half),
            unpackHalf(current1.texCoord0Half),
            unpackHalf(current2.texCoord0Half),
            weights,
            flatQualified)
        : float2(0.0);
    float2 uv1 =
        requiresTexCoord1 && hasTexCoord1
            ? XR_ADV_Interpolate(
                unpackHalf(current0.texCoord1Half),
                unpackHalf(current1.texCoord1Half),
                unpackHalf(current2.texCoord1Half),
                weights,
                flatQualified)
            : requiresTexCoord1 ? uv0 : float2(0.0);
    float4 color0 =
        (requiredMask & XR_ADV_ATTRIBUTE_COLOR0) != 0u
            ? XR_ADV_Interpolate(
                XR_ADV_UnpackRgba8(current0.color0Rgba8),
                XR_ADV_UnpackRgba8(current1.color0Rgba8),
                XR_ADV_UnpackRgba8(current2.color0Rgba8),
                weights,
                flatQualified)
            : float4(1.0);
    float4 color1 =
        (requiredMask & XR_ADV_ATTRIBUTE_COLOR1) != 0u
            ? XR_ADV_Interpolate(
                XR_ADV_UnpackRgba8(current0.color1Rgba8),
                XR_ADV_UnpackRgba8(current1.color1Rgba8),
                XR_ADV_UnpackRgba8(current2.color1Rgba8),
                weights,
                flatQualified)
            : float4(1.0);
    float2 uv0Dx = !derivativesRequested || flatQualified
        ? float2(0.0)
        : XR_ADV_Gradient(
            unpackHalf(current0.texCoord0Half),
            unpackHalf(current1.texCoord0Half),
            unpackHalf(current2.texCoord0Half),
            weightDx);
    float2 uv0Dy = !derivativesRequested || flatQualified
        ? float2(0.0)
        : XR_ADV_Gradient(
            unpackHalf(current0.texCoord0Half),
            unpackHalf(current1.texCoord0Half),
            unpackHalf(current2.texCoord0Half),
            weightDy);
    float2 uv1Dx = !derivativesRequested || flatQualified
        ? float2(0.0)
        : hasTexCoord1
            ? XR_ADV_Gradient(
                unpackHalf(current0.texCoord1Half),
                unpackHalf(current1.texCoord1Half),
                unpackHalf(current2.texCoord1Half),
                weightDx)
            : uv0Dx;
    float2 uv1Dy = !derivativesRequested || flatQualified
        ? float2(0.0)
        : hasTexCoord1
            ? XR_ADV_Gradient(
                unpackHalf(current0.texCoord1Half),
                unpackHalf(current1.texCoord1Half),
                unpackHalf(current2.texCoord1Half),
                weightDy)
            : uv0Dy;

    float3 interpolatedCurrentWorld = XR_ADV_Interpolate(
        currentWorld0,
        currentWorld1,
        currentWorld2,
        weights,
        false);
    float3 worldPosition =
        (requiredMask & XR_ADV_ATTRIBUTE_DEFORMED_POSITION) != 0u
            ? interpolatedCurrentWorld
            : XR_ADV_ReconstructWorldPositionFromDepth(
                pixelCenter,
                depth,
                viewRecord);
    float4 currentUnjitteredClip =
        mul(float4(interpolatedCurrentWorld, 1.0), viewRecord.viewProjectionUnjittered);
    float4 previousUnjitteredClip = float4(0.0);
    bool viewHistoryValid =
        (viewRecord.flags & XR_ADV_VIEW_TEMPORAL_HISTORY_VALID) != 0u;
    if (!viewHistoryValid && velocityValidityReason == 0u)
        velocityValidityReason = 7u; // EAdvancedVelocityValidityReason.HistoryReset
    bool velocityValid =
        ((metadata >> XR_ADV_VIS_VELOCITY_VALID_SHIFT) & 1u) != 0u &&
        viewHistoryValid &&
        previousGeometryValid &&
        velocityValidityReason == 0u;
    if (previousGeometryValid)
    {
        float3 previousWorld0 =
            XR_ADV_TransformPositionRowVector(
                previous0.position,
                previousTransform.world);
        float3 previousWorld1 =
            XR_ADV_TransformPositionRowVector(
                previous1.position,
                previousTransform.world);
        float3 previousWorld2 =
            XR_ADV_TransformPositionRowVector(
                previous2.position,
                previousTransform.world);
        float3 previousWorld = XR_ADV_Interpolate(
            previousWorld0,
            previousWorld1,
            previousWorld2,
            weights,
            false);
        previousUnjitteredClip =
            mul(float4(previousWorld, 1.0), viewRecord.previousViewProjectionUnjittered);
        velocityValid = velocityValid &&
            XR_ADV_IsFinite(previousUnjitteredClip) &&
            abs(previousUnjitteredClip.w) >
                XR_ADV_RECONSTRUCTION_MIN_W;
    }
    velocityValid = velocityValid &&
        XR_ADV_IsFinite(currentUnjitteredClip) &&
        abs(currentUnjitteredClip.w) >
            XR_ADV_RECONSTRUCTION_MIN_W;

    bool masked =
        ((metadata >> XR_ADV_VIS_MASKED_SHIFT) & 1u) != 0u;
    bool frontFacing =
        ((metadata >> XR_ADV_VIS_FRONT_FACE_SHIFT) & 1u) != 0u;
    bool reactive = masked || !velocityValid;
    uint flags = XR_ADV_SURFACE_VALID |
        ((velocityValidityReason << XR_ADV_TEMPORAL_VELOCITY_REASON_SHIFT) &
            XR_ADV_TEMPORAL_VELOCITY_REASON_MASK);
    if (derivativesRequested)
        flags |= XR_ADV_SURFACE_DERIVATIVES_VALID;
    if (!frontFacing)
        flags |= XR_ADV_SURFACE_BACK_FACING;
    if (flatQualified)
        flags |= XR_ADV_SURFACE_FLAT_ATTRIBUTES;
    if (deformed)
        flags |= XR_ADV_SURFACE_DEFORMED;
    if (velocityValid)
        flags |= XR_ADV_SURFACE_VELOCITY_VALID;
    if (masked)
        flags |= XR_ADV_SURFACE_MASKED_EDGE;
    if (reactive)
        flags |= XR_ADV_SURFACE_REACTIVE;
    if (mirrored)
        flags |= XR_ADV_SURFACE_MIRRORED_TRANSFORM;

    surface.identity = identity;
    surface.producer = producer;
    surface.viewIndex = viewIndex;
    surface.materialDenseIndex = materialDense;
    surface.shadingKernelDenseIndex = kernelDense;
    surface.primitiveSection = draw.primitiveSection;
    surface.layerMask = instance.layerMask;
    surface.authoredRenderPass = instance.reserved0;
    surface.requiredAttributeMask = requiredMask;
    surface.worldPosition = worldPosition;
    surface.interpolatedWorldPosition = interpolatedCurrentWorld;
    surface.worldPositionDx = currentWorld0 * weightDx.x + currentWorld1 * weightDx.y + currentWorld2 * weightDx.z;
    surface.worldPositionDy = currentWorld0 * weightDy.x + currentWorld1 * weightDy.y + currentWorld2 * weightDy.z;
    surface.visibilityMetadata = metadata;
    surface.samplePixelPosition = pixelCenter;
    surface.geometricNormal = geometricNormal;
    surface.shadingNormal = shadingNormal;
    surface.tangent = tangent;
    surface.bitangent = bitangent;
    surface.texCoord0 = uv0;
    surface.texCoord1 = uv1;
    surface.color0 = color0;
    surface.color1 = color1;
    surface.custom0 =
        (requiredMask & XR_ADV_ATTRIBUTE_CUSTOM0) != 0u
        ? current0.custom0
        : 0u;
    surface.barycentrics = weights;
    surface.barycentricDx = weightDx;
    surface.barycentricDy = weightDy;
    float foveationScale = XR_ADV_FoveationDerivativeScale(
        viewRecord, pixelCenter * viewRecord.renderSizeAndInverse.zw);
    surface.texCoord0Dx = uv0Dx * foveationScale;
    surface.texCoord0Dy = uv0Dy * foveationScale;
    surface.texCoord1Dx = uv1Dx * foveationScale;
    surface.texCoord1Dy = uv1Dy * foveationScale;
    surface.currentUnjitteredClip = currentUnjitteredClip;
    surface.previousUnjitteredClip = previousUnjitteredClip;
    surface.motionNdc = velocityValid
        ? clamp(
            currentUnjitteredClip.xy /
                currentUnjitteredClip.w -
            previousUnjitteredClip.xy /
                previousUnjitteredClip.w,
            float2(-2.0),
            float2(2.0))
        : float2(0.0);
    surface.depth = depth;
    surface.tangentHandedness = handedness;
    surface.flags = flags;
    surface.invalidReason = XR_ADV_RECONSTRUCTION_OK;
    bool finite = XR_ADV_IsFinite(surface.worldPosition) &&
        XR_ADV_IsFinite(surface.shadingNormal) &&
        XR_ADV_IsFinite(surface.tangent) &&
        XR_ADV_IsFinite(surface.bitangent) &&
        XR_ADV_IsFinite(surface.texCoord0) &&
        XR_ADV_IsFinite(surface.texCoord0Dx) &&
        XR_ADV_IsFinite(surface.texCoord0Dy) &&
        XR_ADV_IsFinite(surface.texCoord1Dx) &&
        XR_ADV_IsFinite(surface.texCoord1Dy);
    if (!finite)
    {
        surface.flags = 0u;
        surface.invalidReason =
            XR_ADV_RECONSTRUCTION_NON_FINITE;
    }
    return finite;
}
