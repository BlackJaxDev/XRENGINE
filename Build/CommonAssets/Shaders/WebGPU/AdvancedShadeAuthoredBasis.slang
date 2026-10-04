// Browser-only source companion. Canonical vertex/geometry records stay unchanged.
#if !defined(XR_ADV_AUTHORED_BASIS_SCHEMA_VERSION) || XR_ADV_AUTHORED_BASIS_SCHEMA_VERSION != 1
#error Authored normal reconstruction requires the browser basis schema 1 companion.
#endif
struct XRAdvancedAuthoredBasisVertex
{
    float3 normal;
    uint flags;
    float4 tangent;
};
XRAdvancedAuthoredBasisVertex XR_ADV_ReadAuthoredBasisVertex(uint word)
{
    XRAdvancedAuthoredBasisVertex value;
    value.normal = asfloat(uint3(SceneArena[word], SceneArena[word + 1u], SceneArena[word + 2u]));
    value.flags = SceneArena[word + 3u];
    value.tangent = asfloat(uint4(SceneArena[word + 4u], SceneArena[word + 5u], SceneArena[word + 6u], SceneArena[word + 7u]));
    return value;
}
bool XR_ADV_TryLoadAuthoredBasis(uint drawDense, uint drawIdentity, XRAdvancedDrawRecord draw,
    uint geometryDense, uint3 sourceVertices, out XRAdvancedAuthoredBasisVertex basis0,
    out XRAdvancedAuthoredBasisVertex basis1, out XRAdvancedAuthoredBasisVertex basis2)
{
    basis0 = (XRAdvancedAuthoredBasisVertex)0;
    basis1 = (XRAdvancedAuthoredBasisVertex)0;
    basis2 = (XRAdvancedAuthoredBasisVertex)0;
    uint words, stride;
    SceneArena.GetDimensions(words, stride);
    const uint header = 33u * 8u;
    if (words < header + 8u || SceneArena[header + 2u] != 12u || SceneArena[header + 5u] != 1u)
        return false;
    uint offset = SceneArena[header], count = SceneArena[header + 1u];
    if (drawDense >= count || offset > words || drawDense >= (words - offset) / 12u)
        return false;
    uint row = offset + drawDense * 12u;
    XRAdvancedGpuHandle sourceDraw = XRAdvancedGpuHandle(SceneArena[row], SceneArena[row + 1u]);
    XRAdvancedGpuHandle sourceGeometry = XRAdvancedGpuHandle(SceneArena[row + 2u], SceneArena[row + 3u]);
    if (sourceDraw.index != drawIdentity || sourceGeometry.index != draw.geometry.index ||
        sourceGeometry.generation != draw.geometry.generation ||
        XR_ADV_ResolveVisibilityHandle(sourceDraw, VisibilityDrawLookupSegment, XR_ADV_DIAGNOSTIC_DRAW) != drawDense ||
        XR_ADV_ResolveVisibilityHandle(sourceGeometry, VisibilityGeometryLookupSegment, XR_ADV_DIAGNOSTIC_MESH) != geometryDense)
        return false;
    uint vertexWord = SceneArena[row + 6u], vertexCount = SceneArena[row + 7u];
    if (vertexWord > words || vertexCount > (words - vertexWord) / 8u ||
        any(sourceVertices >= uint3(vertexCount))) return false;
    basis0 = XR_ADV_ReadAuthoredBasisVertex(vertexWord + sourceVertices.x * 8u);
    basis1 = XR_ADV_ReadAuthoredBasisVertex(vertexWord + sourceVertices.y * 8u);
    basis2 = XR_ADV_ReadAuthoredBasisVertex(vertexWord + sourceVertices.z * 8u);
    return ((basis0.flags | basis1.flags | basis2.flags) & ~7u) == 0u &&
        ((basis0.flags ^ basis1.flags) & 1u) == 0u && ((basis0.flags ^ basis2.flags) & 1u) == 0u;
}

XRAdvancedAuthoredBasisVertex XR_ADV_ReadAuthoredDeformedBasisVertex(uint word)
{
    XRAdvancedAuthoredBasisVertex value;
    value.normal = asfloat(uint3(GeometryArena[word], GeometryArena[word + 1u], GeometryArena[word + 2u]));
    value.flags = GeometryArena[word + 3u];
    value.tangent = asfloat(uint4(GeometryArena[word + 4u], GeometryArena[word + 5u], GeometryArena[word + 6u], GeometryArena[word + 7u]));
    return value;
}
bool XR_ADV_TryLoadAuthoredDeformedBasis(bool previous, uint3 indices,
    out XRAdvancedAuthoredBasisVertex basis0, out XRAdvancedAuthoredBasisVertex basis1,
    out XRAdvancedAuthoredBasisVertex basis2)
{
    basis0 = (XRAdvancedAuthoredBasisVertex)0;
    basis1 = (XRAdvancedAuthoredBasisVertex)0;
    basis2 = (XRAdvancedAuthoredBasisVertex)0;
    uint words, stride;
    GeometryArena.GetDimensions(words, stride);
    uint header = (previous ? 3u : 2u) * 4u;
    if (words < header + 4u) return false;
    uint start = GeometryArena[header + 2u], bytes = GeometryArena[header + 3u];
    if (start < 28u || start > words || bytes == 0u || (bytes & 31u) != 0u ||
        bytes / 4u > words - start || any(indices >= uint3(bytes / 32u))) return false;
    basis0 = XR_ADV_ReadAuthoredDeformedBasisVertex(start + indices.x * 8u);
    basis1 = XR_ADV_ReadAuthoredDeformedBasisVertex(start + indices.y * 8u);
    basis2 = XR_ADV_ReadAuthoredDeformedBasisVertex(start + indices.z * 8u);
    return ((basis0.flags | basis1.flags | basis2.flags) & ~7u) == 0u &&
        ((basis0.flags ^ basis1.flags) & 1u) == 0u && ((basis0.flags ^ basis2.flags) & 1u) == 0u;
}
