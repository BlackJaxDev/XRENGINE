using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Immutable metadata supplied while registering one geometry row.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedGeometryRegistration(
    uint VertexCount,
    uint IndexCount,
    uint VertexStride,
    EPrimitiveType PrimitiveTopology,
    ulong VertexLayoutId,
    Vector4 BoundsSphere,
    Vector4 BoundsMin,
    Vector4 BoundsMax,
    uint MaterialSectionFirst,
    uint MaterialSectionCount,
    uint MeshletFirst,
    uint MeshletCount,
    uint CookedLayoutVersion)
{
    public static AdvancedGeometryRegistration Create(
        uint vertexCount,
        uint indexCount,
        uint vertexStride,
        EPrimitiveType primitiveTopology,
        ulong vertexLayoutId,
        Vector4 boundsSphere,
        Vector4 boundsMin,
        Vector4 boundsMax,
        uint materialSectionFirst = 0u,
        uint materialSectionCount = 1u,
        uint meshletFirst = 0u,
        uint meshletCount = 0u)
        => new(
            vertexCount,
            indexCount,
            vertexStride,
            primitiveTopology,
            vertexLayoutId,
            boundsSphere,
            boundsMin,
            boundsMax,
            materialSectionFirst,
            materialSectionCount,
            meshletFirst,
            meshletCount,
            AdvancedGeometryCookedLayout.CurrentVersion);
}
