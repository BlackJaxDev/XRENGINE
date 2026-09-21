using System.Numerics;
using XREngine.Components.Scene.Mesh;

namespace XREngine.Rendering.Compute;

/// <summary>GPU-only source contract for aggregate geometry consumers.</summary>
public readonly record struct GpuMeshBvhGeometrySources(
    XRMeshRenderer Renderer,
    XRMesh Mesh,
    XRDataBuffer? Positions,
    XRDataBuffer? Interleaved,
    XRDataBuffer TriangleIndices,
    uint TriangleCount,
    long GeometryRevision,
    bool UseInterleaved,
    uint InterleavedStrideBytes,
    uint PositionOffsetBytes,
    uint PositionStrideScalars,
    Matrix4x4 LocalToWorld,
    bool IsSkinned,
    bool IsSkinningPending,
    bool IsGpuDeformed,
    string? DeformationDiagnostic)
{
    /// <summary>Current deformed normals, or a GPU storage view of authored normals.</summary>
    public XRDataBuffer? Normals { get; init; }
    public XRDataBuffer? TexCoords0 { get; init; }
    public XRDataBuffer? TexCoords1 { get; init; }
    public bool TexCoordsInterleaved { get; init; }
    public ulong AttributeRevision { get; init; }

    public static GpuMeshBvhGeometrySources PendingGpuDeformation(
        XRMeshRenderer renderer,
        XRMesh mesh,
        uint triangleCount,
        XRDataBuffer triangleIndices,
        bool isSkinned,
        string? diagnostic)
        => new(
            renderer,
            mesh,
            null,
            null,
            triangleIndices,
            triangleCount,
            mesh.GeometryRevision,
            false,
            0u,
            0u,
            0u,
            Matrix4x4.Identity,
            isSkinned,
            true,
            true,
            diagnostic);
}
