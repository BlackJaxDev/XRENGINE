using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Data.Geometry;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Frame-boundary conversion of source triangle meshes into the canonical
/// immutable geometry arenas. Scratch is retained by the publisher so normal
/// frame publication performs no managed allocation after a growth boundary.
/// </summary>
public sealed partial class AdvancedGpuScenePublisher
{
    private AdvancedDeformedVertex[] _packedGeometryVertices = [];
    private uint[] _packedGeometryIndices = [];
    private AdvancedMeshletDescriptor[] _packedMeshletDescriptors = [];
    private uint[] _packedMeshletTriangleWords = [];

    private static bool TryValidateCanonicalGeometry(
        XRMesh? mesh,
        out EAdvancedCanonicalCompatibilityReason reason)
    {
        if (mesh is null || mesh.Type != EPrimitiveType.Triangles)
        {
            reason = EAdvancedCanonicalCompatibilityReason.UnsupportedGeometryTopology;
            return false;
        }

        Vertex[] vertices = mesh.Vertices;
        List<IndexTriangle>? triangles = mesh.Triangles;
        if (vertices.Length == 0 || vertices.Length != mesh.VertexCount ||
            triangles is null || triangles.Count == 0)
        {
            reason = EAdvancedCanonicalCompatibilityReason.InvalidGeometrySource;
            return false;
        }

        for (int triangleIndex = 0; triangleIndex < triangles.Count; ++triangleIndex)
        {
            IndexTriangle triangle = triangles[triangleIndex];
            if ((uint)triangle.Point0 >= (uint)vertices.Length ||
                (uint)triangle.Point1 >= (uint)vertices.Length ||
                (uint)triangle.Point2 >= (uint)vertices.Length)
            {
                reason = EAdvancedCanonicalCompatibilityReason.InvalidGeometrySource;
                return false;
            }
        }

        reason = EAdvancedCanonicalCompatibilityReason.None;
        return true;
    }

    private bool TryRegisterCanonicalGeometry(
        XRMesh? mesh,
        in BoundsGpu bounds,
        in DrawMetadata command,
        out AdvancedGpuHandle geometry)
    {
        geometry = AdvancedGpuHandle.Invalid;
        if (!TryValidateCanonicalGeometry(mesh, out _))
            return false;

        // Validation establishes non-null mesh and source streams above.
        Vertex[] sourceVertices = mesh!.Vertices;
        List<IndexTriangle> sourceTriangles = mesh.Triangles!;
        if (!EnsureGeometryScratchCapacity(
                sourceVertices.Length,
                checked(sourceTriangles.Count * 3),
                mesh.MeshletPayload))
        {
            return false;
        }

        for (int vertexIndex = 0; vertexIndex < sourceVertices.Length; ++vertexIndex)
            _packedGeometryVertices[vertexIndex] = AdvancedPackedVertexCodec.Pack(
                sourceVertices[vertexIndex],
                checked((uint)vertexIndex));

        int indexCursor = 0;
        for (int triangleIndex = 0; triangleIndex < sourceTriangles.Count; ++triangleIndex)
        {
            IndexTriangle triangle = sourceTriangles[triangleIndex];
            _packedGeometryIndices[indexCursor++] = checked((uint)triangle.Point0);
            _packedGeometryIndices[indexCursor++] = checked((uint)triangle.Point1);
            _packedGeometryIndices[indexCursor++] = checked((uint)triangle.Point2);
        }

        AdvancedGeometryRegistration registration = AdvancedGeometryRegistration.Create(
            checked((uint)sourceVertices.Length),
            checked((uint)indexCursor),
            checked((uint)Unsafe.SizeOf<AdvancedDeformedVertex>()),
            EPrimitiveType.Triangles,
            AdvancedDeformedVertex.CanonicalLayoutId,
            bounds.BoundingSphere,
            bounds.AabbMin,
            bounds.AabbMax,
            command.SubmeshID,
            1u);
        ReadOnlySpan<byte> vertexBytes = MemoryMarshal.AsBytes(
            _packedGeometryVertices.AsSpan(0, sourceVertices.Length));
        ReadOnlySpan<uint> indices = _packedGeometryIndices.AsSpan(0, indexCursor);

        MeshletPayload? payload = mesh.MeshletPayload;
        if (payload is not { HasMeshlets: true } || !payload.IsValidatedFor(mesh))
            return Database.Scene.Geometry.TryAddStatic(
                vertexBytes,
                indices,
                registration,
                out geometry);

        int descriptorCount = payload.Meshlets.Length;
        for (int descriptorIndex = 0; descriptorIndex < descriptorCount; ++descriptorIndex)
        {
            CpuMeshletDescriptor source = payload.Meshlets[descriptorIndex];
            _packedMeshletDescriptors[descriptorIndex] = new AdvancedMeshletDescriptor
            {
                BoundsSphere = source.BoundsSphere,
                VertexOffset = source.VertexOffset,
                TriangleByteOffset = source.TriangleOffset,
                VertexCount = source.VertexCount,
                TriangleCount = source.TriangleCount,
                Cone = source.Cone,
                ConeApex = source.ConeApex,
                PackedCone = source.PackedCone,
            };
        }
        PackTriangleWords(
            payload.TriangleIndices.AsSpan(),
            _packedMeshletTriangleWords);
        registration = registration with
        {
            MeshletCount = checked((uint)descriptorCount),
        };
        return Database.Scene.Geometry.TryAddMeshletLocal(
            vertexBytes,
            indices,
            _packedMeshletDescriptors.AsSpan(0, descriptorCount),
            payload.VertexIndices.AsSpan(),
            _packedMeshletTriangleWords.AsSpan(
                0,
                checked((payload.TriangleIndices.Length + 3) / 4)),
            registration,
            out geometry);
    }

    private bool EnsureGeometryScratchCapacity(
        int vertexCount,
        int indexCount,
        MeshletPayload? payload)
    {
        if (vertexCount <= 0 || indexCount <= 0)
            return false;

        EnsureCapacity(ref _packedGeometryVertices, vertexCount);
        EnsureCapacity(ref _packedGeometryIndices, indexCount);
        if (payload is not { HasMeshlets: true })
            return true;

        EnsureCapacity(ref _packedMeshletDescriptors, payload.Meshlets.Length);
        EnsureCapacity(
            ref _packedMeshletTriangleWords,
            checked((payload.TriangleIndices.Length + 3) / 4));
        return true;
    }

    private static void PackTriangleWords(
        ReadOnlySpan<byte> triangleBytes,
        Span<uint> destination)
    {
        destination.Clear();
        for (int byteIndex = 0; byteIndex < triangleBytes.Length; ++byteIndex)
            destination[byteIndex >> 2] |=
                (uint)triangleBytes[byteIndex] << ((byteIndex & 3) * 8);
    }

    private static void EnsureCapacity<T>(ref T[] values, int required)
    {
        if (required <= values.Length)
            return;
        Array.Resize(ref values, checked((int)NextPowerOfTwo(checked((uint)required))));
    }
}
