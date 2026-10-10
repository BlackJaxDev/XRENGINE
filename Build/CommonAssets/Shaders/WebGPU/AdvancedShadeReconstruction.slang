float3x3 NormalMatrix(float3x3 m)
{
    float3 row0 = cross(m[1], m[2]);
    float3 row1 = cross(m[2], m[0]);
    float3 row2 = cross(m[0], m[1]);
    return float3x3(row0, row1, row2) / dot(m[0], row0);
}
float3x3 UberNormalMatrix(float3x3 m)
{
    return float3x3(cross(m[1],m[2]),cross(m[2],m[0]),cross(m[0],m[1]));
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

// Preserve the authored normalize operation without the canonical packed
// reconstruction's small-vector threshold. Zero records carry explicit validity.
float3 XR_ADV_NormalizeEngineVertex(float3 value, bool authored)
{
    if (!authored) return XR_ADV_NormalizeOr(value, float3(0.0));
    float3 normalized = normalize(value);
    return XR_ADV_IsFinite(normalized) && dot(normalized, normalized) > 0.0
        ? normalized : float3(0.0);
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

// Local inputs shared by the main sample and same-primitive shadow helpers.
// Canonical inputs remain local-space values until after interpolation. Engine
// texture inputs carry the independently normalized world-space vertex basis.
struct XRAdvancedPreparedSurfaceBasis
{
    float3 normal0, normal1, normal2;
    float3 tangent0, tangent1, tangent2;
    float3 bitangent0, bitangent1, bitangent2;
    float3 tangentSigns;
    float3 geometricNormal;
    float3x3 normalMatrix;
    float3x3 world3;
    bool requiresNormal;
    bool requiresTangent;
    bool vertexBasis;
    bool flatQualified;
    bool mirrored;
    bool authoredSurfaceBasis;
    bool authoredNormalMap;
    bool uberSurface;
    bool authoredInvalidNormal;
    bool authoredInvalidTangent;
};

bool XR_ADV_TryInterpolateSurfaceBasis(XRAdvancedPreparedSurfaceBasis basis,
    float3 weights, out float3 shadingNormal, out float3 tangent,
    out float3 bitangent, out float handedness)
{
    shadingNormal = basis.geometricNormal;
    if (basis.requiresNormal)
    {
        float3 interpolated = XR_ADV_Interpolate(basis.normal0, basis.normal1,
            basis.normal2, weights, basis.flatQualified);
        shadingNormal = basis.vertexBasis ? interpolated :
            XR_ADV_NormalizeOr(mul(interpolated, basis.normalMatrix), basis.geometricNormal);
    }
    handedness = 1.0;
    if (basis.requiresTangent)
    {
        float sourceHandedness = basis.flatQualified ? basis.tangentSigns.x :
            dot(basis.tangentSigns, weights) < 0.0 ? -1.0 : 1.0;
        tangent = XR_ADV_Interpolate(basis.tangent0, basis.tangent1,
            basis.tangent2, weights, basis.flatQualified);
        if (basis.vertexBasis)
        {
            bitangent = XR_ADV_Interpolate(basis.bitangent0, basis.bitangent1,
                basis.bitangent2, weights, basis.flatQualified);
            handedness = sourceHandedness;
        }
        else
        {
            tangent = XR_ADV_OrthogonalTangent(shadingNormal, mul(tangent, basis.world3));
            handedness = sourceHandedness * (basis.mirrored ? -1.0 : 1.0);
            bitangent = normalize(cross(shadingNormal, tangent)) * handedness;
        }
    }
    else if (basis.vertexBasis)
    {
        tangent = float3(0.0);
        bitangent = float3(0.0);
    }
    else
    {
        tangent = XR_ADV_FallbackTangent(shadingNormal);
        bitangent = normalize(cross(shadingNormal, tangent));
    }
    if (basis.authoredSurfaceBasis)
    {
        // Source invalidity and cancellation at this pixel are separate checks.
        if ((basis.uberSurface || !basis.authoredNormalMap) &&
            (basis.authoredInvalidNormal || dot(shadingNormal, shadingNormal) == 0.0))
            return false;
        if (basis.authoredInvalidNormal) shadingNormal = float3(0.0);
        if (basis.authoredInvalidTangent)
        {
            tangent = basis.uberSurface ? float3(0.0) : shadingNormal;
            bitangent = basis.uberSurface ? float3(0.0) : shadingNormal;
        }
    }
    return true;
}

struct XRAdvancedPreparedSurfaceTexCoords
{
    float2 uv00, uv01, uv02;
    float2 uv10, uv11, uv12;
    bool hasTexCoord1;
};

void XR_ADV_InterpolateSurfaceTexCoords(XRAdvancedPreparedSurfaceTexCoords source,
    uint requiredMask, bool flatQualified, bool uberSurface,
    float3 weights, float3 weightDx, float3 weightDy,
    out float2 uv0, out float2 uv1, out float2 uv0Dx, out float2 uv0Dy,
    out float2 uv1Dx, out float2 uv1Dy)
{
    bool derivativesRequested = (requiredMask & XR_ADV_ATTRIBUTE_ANALYTICAL_DERIVATIVES) != 0u;
    bool texCoord0Requested = (requiredMask & XR_ADV_ATTRIBUTE_TEXCOORD0) != 0u || derivativesRequested;
    bool requiresTexCoord1 = (requiredMask & XR_ADV_ATTRIBUTE_TEXCOORD1) != 0u;
    uv0 = uberSurface || texCoord0Requested ? XR_ADV_Interpolate(
        source.uv00, source.uv01, source.uv02, weights, flatQualified) : float2(0.0);
    uv1 = uberSurface || requiresTexCoord1 && source.hasTexCoord1 ? XR_ADV_Interpolate(
        source.uv10, source.uv11, source.uv12, weights, flatQualified) :
        requiresTexCoord1 ? uv0 : float2(0.0);
    bool gradients = uberSurface || derivativesRequested && !flatQualified;
    uv0Dx = gradients ? XR_ADV_Gradient(source.uv00, source.uv01, source.uv02, weightDx) : float2(0.0);
    uv0Dy = gradients ? XR_ADV_Gradient(source.uv00, source.uv01, source.uv02, weightDy) : float2(0.0);
    uv1Dx = !gradients ? float2(0.0) : uberSurface || source.hasTexCoord1 ?
        XR_ADV_Gradient(source.uv10, source.uv11, source.uv12, weightDx) : uv0Dx;
    uv1Dy = !gradients ? float2(0.0) : uberSurface || source.hasTexCoord1 ?
        XR_ADV_Gradient(source.uv10, source.uv11, source.uv12, weightDy) : uv0Dy;
}

bool XR_ADV_ReconstructedSurfaceIsFinite(XRAdvancedSurface surface)
{
    return XR_ADV_IsFinite(surface.worldPosition) &&
        XR_ADV_IsFinite(surface.shadingNormal) &&
        XR_ADV_IsFinite(surface.tangent) &&
        XR_ADV_IsFinite(surface.bitangent) &&
        XR_ADV_IsFinite(surface.texCoord0) &&
        XR_ADV_IsFinite(surface.texCoord0Dx) &&
        XR_ADV_IsFinite(surface.texCoord0Dy) &&
        XR_ADV_IsFinite(surface.texCoord1Dx) &&
        XR_ADV_IsFinite(surface.texCoord1Dy);
}

#if !defined(XR_ADV_NATIVE_MODIFIERS_ABSENT_SCHEMA_VERSION) && !defined(XR_ADV_UBER_RASTER_SURFACE_SCHEMA_VERSION)
#define XR_ADV_PREPARED_SHADOW_PRIMITIVE 1
// Function-local invocation state, never part of a buffer or surface ABI. It
// becomes ready only after the complete main reconstruction passes its checks.
struct XRAdvancedPreparedShadowPrimitive
{
    float4 clip0, clip1, clip2;
    float3 world0, world1, world2;
    XRAdvancedPreparedSurfaceBasis basis;
    XRAdvancedPreparedSurfaceTexCoords texCoords;
    bool ready;
};
#endif

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

#if defined(XR_ADV_PREPARED_SHADOW_PRIMITIVE)
bool XR_WEB_TryInterpolateShadowSurface(XRAdvancedSurface surface, float2 pixelCenter,
    __constref XRAdvancedPreparedShadowPrimitive prepared, out XRAdvancedSurface helper)
{
    // This surface is used only by receiver-normal evaluation. The main sample
    // retains ownership of color, previous geometry and temporal outputs.
    helper = surface;
    if (!prepared.ready) return false;
    XRAdvancedViewRecord viewRecord = XR_ADV_LoadView(surface.viewIndex);
    float3 weights, weightDx, weightDy;
    if (!XR_ADV_TryPerspectiveBarycentrics(pixelCenter,
        prepared.clip0, prepared.clip1, prepared.clip2, viewRecord,
        weights, weightDx, weightDy))
        return false;
    float3 shadingNormal, tangent, bitangent;
    float handedness;
    if (!XR_ADV_TryInterpolateSurfaceBasis(prepared.basis, weights,
        shadingNormal, tangent, bitangent, handedness))
        return false;
    float2 uv0, uv1, uv0Dx, uv0Dy, uv1Dx, uv1Dy;
    XR_ADV_InterpolateSurfaceTexCoords(prepared.texCoords, surface.requiredAttributeMask,
        prepared.basis.flatQualified, prepared.basis.uberSurface,
        weights, weightDx, weightDy, uv0, uv1, uv0Dx, uv0Dy, uv1Dx, uv1Dy);
    float foveationScale = XR_ADV_FoveationDerivativeScale(
        viewRecord, pixelCenter * viewRecord.renderSizeAndInverse.zw);
    helper.interpolatedWorldPosition = XR_ADV_Interpolate(
        prepared.world0, prepared.world1, prepared.world2, weights, false);
    // Keep the original depth-position finite check before the caller replaces
    // this position with the interpolated primitive position for shadowing.
    helper.worldPosition = prepared.basis.uberSurface ||
        (surface.requiredAttributeMask & XR_ADV_ATTRIBUTE_DEFORMED_POSITION) != 0u
        ? helper.interpolatedWorldPosition
        : XR_ADV_ReconstructWorldPositionFromDepth(pixelCenter, surface.depth, viewRecord);
    helper.worldPositionDx = prepared.world0 * weightDx.x + prepared.world1 * weightDx.y + prepared.world2 * weightDx.z;
    helper.worldPositionDy = prepared.world0 * weightDy.x + prepared.world1 * weightDy.y + prepared.world2 * weightDy.z;
    helper.samplePixelPosition = pixelCenter;
    helper.geometricNormal = prepared.basis.geometricNormal;
    helper.shadingNormal = shadingNormal;
    helper.tangent = tangent;
    helper.bitangent = bitangent;
    helper.tangentHandedness = handedness;
    helper.texCoord0 = uv0;
    helper.texCoord1 = uv1;
    helper.texCoord0Dx = uv0Dx * foveationScale;
    helper.texCoord0Dy = uv0Dy * foveationScale;
    helper.texCoord1Dx = uv1Dx * foveationScale;
    helper.texCoord1Dy = uv1Dy * foveationScale;
    helper.barycentrics = weights;
    helper.barycentricDx = weightDx;
    helper.barycentricDy = weightDy;
    return XR_ADV_ReconstructedSurfaceIsFinite(helper);
}
#endif

bool XR_ADV_TryReconstructSurface(
    uint2 identity,
    uint metadata,
    float depth,
    float2 pixelCenter,
#if defined(XR_ADV_PREPARED_SHADOW_PRIMITIVE)
    out XRAdvancedPreparedShadowPrimitive preparedPrimitive,
#endif
    out XRAdvancedSurface surface)
{
#if defined(XR_ADV_PREPARED_SHADOW_PRIMITIVE)
    preparedPrimitive = (XRAdvancedPreparedShadowPrimitive)0;
#endif
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
    uint uberWord, uberFeatures, uberPipeline;
    bool uberSurface = XR_ADV_IsUberBaseMaterial(material);
    if (uberSurface && !XR_ADV_TryLoadUberBase(material,uberWord,uberFeatures,uberPipeline))
    { surface.invalidReason=XR_ADV_RECONSTRUCTION_GEOMETRY_MISSING; return false; }
    if (uberSurface) flatQualified=false;
    bool engineTextureSurface = XR_ADV_TryLoadEngineSurfaceHeader(material,
        engineSurfaceWord, engineSurfaceKind, engineSurfaceRoles) &&
        engineSurfaceKind != XR_ADV_ENGINE_SURFACE_COLOR;
    bool authoredSurfaceBasis = uberSurface || engineTextureSurface &&
        engineSurfaceKind == XR_ADV_ENGINE_SURFACE_AUTHORED_TEXTURED;
    bool authoredNormalMap = uberSurface ? (uberFeatures & 1u)!=0u : authoredSurfaceBasis && (engineSurfaceRoles & 2u) != 0u;
    engineTextureSurface = engineTextureSurface || uberSurface;
    XRAdvancedAuthoredBasisVertex basis0, basis1, basis2;
    bool authoredInvalidNormal = false;
    bool authoredInvalidTangent = false;
    if (authoredSurfaceBasis)
    {
        if (!XR_ADV_TryLoadAuthoredBasis(drawDense, identity.x, draw, geometryDense, sourceVertices, basis0, basis1, basis2))
        {
            surface.invalidReason = XR_ADV_RECONSTRUCTION_GEOMETRY_MISSING;
            return false;
        }
        uint sourcePresence = basis0.flags & 1u;
        if (deformed && (!XR_ADV_TryLoadAuthoredDeformedBasis(false, currentIndices, basis0, basis1, basis2) ||
            (basis0.flags & 1u) != sourcePresence))
        {
            surface.invalidReason = XR_ADV_RECONSTRUCTION_CURRENT_GEOMETRY_MISSING;
            return false;
        }
        requiresTangent = sourcePresence != 0u;
        uint commonFlags = basis0.flags & basis1.flags & basis2.flags;
        authoredInvalidNormal = (commonFlags & 2u) == 0u;
        authoredInvalidTangent = requiresTangent && (commonFlags & 4u) == 0u;
    }
    XRAdvancedPreparedSurfaceBasis preparedBasis = (XRAdvancedPreparedSurfaceBasis)0;
    preparedBasis.geometricNormal = geometricNormal;
    preparedBasis.world3 = world3;
    preparedBasis.requiresNormal = (requiredMask & XR_ADV_ATTRIBUTE_NORMAL) != 0u;
    preparedBasis.requiresTangent = requiresTangent;
    preparedBasis.vertexBasis = engineTextureSurface || uberSurface;
    preparedBasis.flatQualified = flatQualified;
    preparedBasis.mirrored = mirrored;
    preparedBasis.authoredSurfaceBasis = authoredSurfaceBasis;
    preparedBasis.authoredNormalMap = authoredNormalMap;
    preparedBasis.uberSurface = uberSurface;
    if (preparedBasis.requiresNormal)
    {
        float3x3 normalMatrix =
            uberSurface ? UberNormalMatrix(world3) : NormalMatrix(world3);
        preparedBasis.normalMatrix = normalMatrix;
        if (preparedBasis.vertexBasis)
        {
            // Normalize each transformed vertex before perspective interpolation.
            preparedBasis.normal0 = XR_ADV_NormalizeEngineVertex(mul(authoredSurfaceBasis ? basis0.normal : XR_ADV_DecodeOct(current0.normalOct), normalMatrix), authoredSurfaceBasis);
            preparedBasis.normal1 = XR_ADV_NormalizeEngineVertex(mul(authoredSurfaceBasis ? basis1.normal : XR_ADV_DecodeOct(current1.normalOct), normalMatrix), authoredSurfaceBasis);
            preparedBasis.normal2 = XR_ADV_NormalizeEngineVertex(mul(authoredSurfaceBasis ? basis2.normal : XR_ADV_DecodeOct(current2.normalOct), normalMatrix), authoredSurfaceBasis);
            authoredInvalidNormal = authoredInvalidNormal || authoredSurfaceBasis &&
                (dot(preparedBasis.normal0, preparedBasis.normal0) == 0.0 ||
                 dot(preparedBasis.normal1, preparedBasis.normal1) == 0.0 ||
                 dot(preparedBasis.normal2, preparedBasis.normal2) == 0.0);
        }
        else
        {
            preparedBasis.normal0 = XR_ADV_DecodeOct(current0.normalOct);
            preparedBasis.normal1 = XR_ADV_DecodeOct(current1.normalOct);
            preparedBasis.normal2 = XR_ADV_DecodeOct(current2.normalOct);
        }
    }
    if (requiresTangent)
    {
        float tangentSign0, tangentSign1, tangentSign2;
        float3 localTangent0 = XR_ADV_DecodeTangent(current0.tangentOctAndSign, tangentSign0);
        float3 localTangent1 = XR_ADV_DecodeTangent(current1.tangentOctAndSign, tangentSign1);
        float3 localTangent2 = XR_ADV_DecodeTangent(current2.tangentOctAndSign, tangentSign2);
        if (authoredSurfaceBasis)
        {
            localTangent0 = basis0.tangent.xyz;
            localTangent1 = basis1.tangent.xyz;
            localTangent2 = basis2.tangent.xyz;
            tangentSign0 = basis0.tangent.w;
            tangentSign1 = basis1.tangent.w;
            tangentSign2 = basis2.tangent.w;
        }
        preparedBasis.tangentSigns = float3(tangentSign0, tangentSign1, tangentSign2);
        if (preparedBasis.vertexBasis)
        {
            // Transform and normalize tangent and bitangent independently. Keep
            // source-degenerate values distinct from absent tangent attributes.
            float3x3 normalMatrix = uberSurface ? UberNormalMatrix(world3) : NormalMatrix(world3);
            preparedBasis.tangent0 = XR_ADV_NormalizeEngineVertex(mul(localTangent0, normalMatrix), authoredSurfaceBasis);
            preparedBasis.tangent1 = XR_ADV_NormalizeEngineVertex(mul(localTangent1, normalMatrix), authoredSurfaceBasis);
            preparedBasis.tangent2 = XR_ADV_NormalizeEngineVertex(mul(localTangent2, normalMatrix), authoredSurfaceBasis);
            preparedBasis.bitangent0 = XR_ADV_NormalizeEngineVertex(mul(
                cross(authoredSurfaceBasis ? basis0.normal : XR_ADV_DecodeOct(current0.normalOct), localTangent0) * tangentSign0,
                normalMatrix), authoredSurfaceBasis);
            preparedBasis.bitangent1 = XR_ADV_NormalizeEngineVertex(mul(
                cross(authoredSurfaceBasis ? basis1.normal : XR_ADV_DecodeOct(current1.normalOct), localTangent1) * tangentSign1,
                normalMatrix), authoredSurfaceBasis);
            preparedBasis.bitangent2 = XR_ADV_NormalizeEngineVertex(mul(
                cross(authoredSurfaceBasis ? basis2.normal : XR_ADV_DecodeOct(current2.normalOct), localTangent2) * tangentSign2,
                normalMatrix), authoredSurfaceBasis);
            authoredInvalidTangent = authoredInvalidTangent || authoredSurfaceBasis &&
                (dot(preparedBasis.tangent0, preparedBasis.tangent0) == 0.0 ||
                 dot(preparedBasis.tangent1, preparedBasis.tangent1) == 0.0 ||
                 dot(preparedBasis.tangent2, preparedBasis.tangent2) == 0.0 ||
                 dot(preparedBasis.bitangent0, preparedBasis.bitangent0) == 0.0 ||
                 dot(preparedBasis.bitangent1, preparedBasis.bitangent1) == 0.0 ||
                 dot(preparedBasis.bitangent2, preparedBasis.bitangent2) == 0.0);
        }
        else
        {
            preparedBasis.tangent0 = localTangent0;
            preparedBasis.tangent1 = localTangent1;
            preparedBasis.tangent2 = localTangent2;
        }
    }
    preparedBasis.authoredInvalidNormal = authoredInvalidNormal;
    preparedBasis.authoredInvalidTangent = authoredInvalidTangent;
    float3 shadingNormal, tangent, bitangent;
    float handedness;
    if (!XR_ADV_TryInterpolateSurfaceBasis(preparedBasis, weights,
        shadingNormal, tangent, bitangent, handedness))
    {
        surface.invalidReason = XR_ADV_RECONSTRUCTION_CURRENT_GEOMETRY_MISSING;
        return false;
    }

    bool derivativesRequested =
        (requiredMask &
         XR_ADV_ATTRIBUTE_ANALYTICAL_DERIVATIVES) != 0u;
    XRAdvancedPreparedSurfaceTexCoords preparedTexCoords;
    preparedTexCoords.uv00 = unpackHalf(current0.texCoord0Half);
    preparedTexCoords.uv01 = unpackHalf(current1.texCoord0Half);
    preparedTexCoords.uv02 = unpackHalf(current2.texCoord0Half);
    preparedTexCoords.uv10 = unpackHalf(current0.texCoord1Half);
    preparedTexCoords.uv11 = unpackHalf(current1.texCoord1Half);
    preparedTexCoords.uv12 = unpackHalf(current2.texCoord1Half);
    preparedTexCoords.hasTexCoord1 = hasTexCoord1;
    float2 uv0, uv1, uv0Dx, uv0Dy, uv1Dx, uv1Dy;
    XR_ADV_InterpolateSurfaceTexCoords(preparedTexCoords, requiredMask,
        flatQualified, false, weights, weightDx, weightDy,
        uv0, uv1, uv0Dx, uv0Dy, uv1Dx, uv1Dy);
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
    float foveationScale = XR_ADV_FoveationDerivativeScale(
        viewRecord, pixelCenter * viewRecord.renderSizeAndInverse.zw);
    if (uberSurface)
    {
        XRAdvancedUberAttributes a,b,c;
        if(!XR_ADV_TryLoadUberAttributes(drawDense,identity.x,draw,geometryDense,sourceVertices,a,b,c))
        {surface.invalidReason=XR_ADV_RECONSTRUCTION_GEOMETRY_MISSING;return false;}
        preparedTexCoords.uv00=a.uv0; preparedTexCoords.uv01=b.uv0; preparedTexCoords.uv02=c.uv0;
        preparedTexCoords.uv10=a.uv1; preparedTexCoords.uv11=b.uv1; preparedTexCoords.uv12=c.uv1;
        XR_ADV_InterpolateSurfaceTexCoords(preparedTexCoords, requiredMask,
            false, true, weights, weightDx, weightDy,
            uv0, uv1, uv0Dx, uv0Dy, uv1Dx, uv1Dy);
        color0=XR_ADV_Interpolate(a.color,b.color,c.color,weights,false);
        surface.uberTexCoord2=XR_ADV_Interpolate(a.uv2,b.uv2,c.uv2,weights,false);
        surface.uberTexCoord3=XR_ADV_Interpolate(a.uv3,b.uv3,c.uv3,weights,false);
        surface.uberTexCoord2Dx=XR_ADV_Gradient(a.uv2,b.uv2,c.uv2,weightDx)*foveationScale;
        surface.uberTexCoord2Dy=XR_ADV_Gradient(a.uv2,b.uv2,c.uv2,weightDy)*foveationScale;
        surface.uberTexCoord3Dx=XR_ADV_Gradient(a.uv3,b.uv3,c.uv3,weightDx)*foveationScale;
        surface.uberTexCoord3Dy=XR_ADV_Gradient(a.uv3,b.uv3,c.uv3,weightDy)*foveationScale;
        surface.uberColorDx=(a.color*weightDx.x+b.color*weightDx.y+c.color*weightDx.z)*foveationScale;
        surface.uberColorDy=(a.color*weightDy.x+b.color*weightDy.y+c.color*weightDy.z)*foveationScale;
        surface.uberLocalPosition=XR_ADV_Interpolate(current0.position,current1.position,current2.position,weights,false);
        surface.uberLocalPositionDx=(current0.position*weightDx.x+current1.position*weightDx.y+current2.position*weightDx.z)*foveationScale;
        surface.uberLocalPositionDy=(current0.position*weightDy.x+current1.position*weightDy.y+current2.position*weightDy.z)*foveationScale;
    }
    float3 interpolatedCurrentWorld = XR_ADV_Interpolate(
        currentWorld0,
        currentWorld1,
        currentWorld2,
        weights,
        false);
    float3 worldPosition =
        uberSurface || (requiredMask & XR_ADV_ATTRIBUTE_DEFORMED_POSITION) != 0u
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
    bool finite = XR_ADV_ReconstructedSurfaceIsFinite(surface);
    if (!finite)
    {
        surface.flags = 0u;
        surface.invalidReason =
            XR_ADV_RECONSTRUCTION_NON_FINITE;
    }
#if defined(XR_ADV_PREPARED_SHADOW_PRIMITIVE)
    if (finite)
    {
        preparedPrimitive.clip0 = currentClip0; preparedPrimitive.clip1 = currentClip1; preparedPrimitive.clip2 = currentClip2;
        preparedPrimitive.world0 = currentWorld0; preparedPrimitive.world1 = currentWorld1; preparedPrimitive.world2 = currentWorld2;
        preparedPrimitive.basis = preparedBasis;
        preparedPrimitive.texCoords = preparedTexCoords;
        preparedPrimitive.ready = true;
    }
#endif
    return finite;
}
