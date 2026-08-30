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
        if (!HasGeometryScratchCapacity(
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

    /// <summary>
    /// Preflights every immutable geometry append while growth is still legal.
    /// The publication transaction that follows may mutate only fixed storage.
    /// </summary>
    private bool TryEnsurePlannedGeometryBoundaryCapacity()
    {
        AdvancedGeometryDatabase geometry = Database.Scene.Geometry;
        ulong staticVertexEnd = geometry.StaticVertexArena.CountBytes;
        ulong indexEnd = geometry.IndexArena.CountBytes;
        ulong meshletDescriptorEnd = geometry.MeshletDescriptorArena.CountBytes;
        ulong meshletVertexIndexEnd = geometry.MeshletVertexIndexArena.CountBytes;
        ulong meshletTriangleWordEnd = geometry.MeshletTriangleWordArena.CountBytes;
        int maximumVertexCount = 0;
        int maximumIndexCount = 0;
        int maximumMeshletDescriptorCount = 0;
        int maximumMeshletTriangleWordCount = 0;
        uint vertexStride = checked((uint)Unsafe.SizeOf<AdvancedDeformedVertex>());
        uint meshletDescriptorStride =
            checked((uint)Unsafe.SizeOf<AdvancedMeshletDescriptor>());

        for (int commandIndex = 0;
             commandIndex < _plannedCommandCount;
             ++commandIndex)
        {
            ref readonly AdvancedGpuSceneCommandTransition plan =
                ref _plannedCommands[commandIndex];
            if (!plan.Supported || !RequiresGeometryAppend(in plan))
                continue;
            if (!TryValidateCanonicalGeometry(plan.Mesh, out _))
                return false;

            XRMesh mesh = plan.Mesh!;
            int vertexCount = mesh.Vertices.Length;
            int indexCount = checked(mesh.Triangles!.Count * 3);
            maximumVertexCount = Math.Max(maximumVertexCount, vertexCount);
            maximumIndexCount = Math.Max(maximumIndexCount, indexCount);

            MeshletPayload? payload = mesh.MeshletPayload;
            if (payload is { HasMeshlets: true })
            {
                maximumMeshletDescriptorCount = Math.Max(
                    maximumMeshletDescriptorCount,
                    payload.Meshlets.Length);
                maximumMeshletTriangleWordCount = Math.Max(
                    maximumMeshletTriangleWordCount,
                    checked((int)(((long)payload.TriangleIndices.Length + 3L) / 4L)));
            }

            if (!TryAccumulateArenaAppend(
                    ref staticVertexEnd,
                    checked((ulong)vertexCount * vertexStride),
                    vertexStride) ||
                !TryAccumulateArenaAppend(
                    ref indexEnd,
                    checked((ulong)indexCount * sizeof(uint)),
                    sizeof(uint)))
            {
                return false;
            }

            if (payload is not { HasMeshlets: true } ||
                !payload.IsValidatedFor(mesh))
            {
                continue;
            }

            if (!TryAccumulateArenaAppend(
                    ref meshletDescriptorEnd,
                    checked((ulong)payload.Meshlets.Length * meshletDescriptorStride),
                    meshletDescriptorStride) ||
                !TryAccumulateArenaAppend(
                    ref meshletVertexIndexEnd,
                    checked((ulong)payload.VertexIndices.Length * sizeof(uint)),
                    sizeof(uint)) ||
                !TryAccumulateArenaAppend(
                    ref meshletTriangleWordEnd,
                    checked(
                        ((ulong)payload.TriangleIndices.Length + 3UL) /
                        4UL * sizeof(uint)),
                    sizeof(uint)))
            {
                return false;
            }
        }

        EnsureCapacity(ref _packedGeometryVertices, maximumVertexCount);
        EnsureCapacity(ref _packedGeometryIndices, maximumIndexCount);
        EnsureCapacity(
            ref _packedMeshletDescriptors,
            maximumMeshletDescriptorCount);
        EnsureCapacity(
            ref _packedMeshletTriangleWords,
            maximumMeshletTriangleWordCount);

        uint requiredStaticVertexBytes = checked((uint)staticVertexEnd);
        uint requiredIndexBytes = checked((uint)indexEnd);
        uint requiredMeshletDescriptorBytes =
            checked((uint)meshletDescriptorEnd);
        uint requiredMeshletVertexIndexBytes =
            checked((uint)meshletVertexIndexEnd);
        uint requiredMeshletTriangleWordBytes =
            checked((uint)meshletTriangleWordEnd);
        if (requiredStaticVertexBytes <= geometry.StaticVertexArena.CapacityBytes &&
            requiredIndexBytes <= geometry.IndexArena.CapacityBytes &&
            requiredMeshletDescriptorBytes <= geometry.MeshletDescriptorArena.CapacityBytes &&
            requiredMeshletVertexIndexBytes <= geometry.MeshletVertexIndexArena.CapacityBytes &&
            requiredMeshletTriangleWordBytes <= geometry.MeshletTriangleWordArena.CapacityBytes)
        {
            return true;
        }

        AdvancedGpuSceneCapacityProfile sceneCapacities =
            Database.Capacities.Scene with
        {
            StaticVertexBytes = GetArenaBoundaryCapacity(
                geometry.StaticVertexArena.CapacityBytes,
                requiredStaticVertexBytes),
            IndexBytes = GetArenaBoundaryCapacity(
                geometry.IndexArena.CapacityBytes,
                requiredIndexBytes),
            MeshletDescriptorBytes = GetArenaBoundaryCapacity(
                geometry.MeshletDescriptorArena.CapacityBytes,
                requiredMeshletDescriptorBytes),
            MeshletVertexIndexBytes = GetArenaBoundaryCapacity(
                geometry.MeshletVertexIndexArena.CapacityBytes,
                requiredMeshletVertexIndexBytes),
            MeshletTriangleWordBytes = GetArenaBoundaryCapacity(
                geometry.MeshletTriangleWordArena.CapacityBytes,
                requiredMeshletTriangleWordBytes),
        };
        return Database.TryGrowGeometryArenasAtFrameBoundary(
            in sceneCapacities);
    }

    private bool RequiresGeometryAppend(
        in AdvancedGpuSceneCommandTransition plan)
    {
        if (plan.RegistrationIndex < 0)
            return true;

        ref readonly AdvancedResidentRegistration registration =
            ref _registrations[plan.RegistrationIndex];
        AdvancedGpuHandle existingMaterial =
            _plannedMaterials[plan.MaterialPlanIndex].ExistingHandle;
        return plan.StructuralSignature != registration.StructuralSignature ||
            !existingMaterial.IsValid ||
            registration.Material != existingMaterial;
    }

    private static bool TryAccumulateArenaAppend(
        ref ulong end,
        ulong byteCount,
        uint elementStride)
    {
        if (byteCount == 0UL || elementStride == 0u ||
            byteCount % elementStride != 0UL)
        {
            return false;
        }

        ulong remainder = end % elementStride;
        if (remainder != 0UL)
            end = checked(end + elementStride - remainder);
        end = checked(end + byteCount);
        return end <= int.MaxValue;
    }

    private static uint GetArenaBoundaryCapacity(
        uint currentCapacity,
        uint requiredCapacity)
    {
        if (requiredCapacity <= currentCapacity)
            return currentCapacity;

        ulong doubled = (ulong)currentCapacity * 2UL;
        return checked((uint)Math.Min(
            int.MaxValue,
            Math.Max(doubled, requiredCapacity)));
    }

    private bool HasGeometryScratchCapacity(
        int vertexCount,
        int indexCount,
        MeshletPayload? payload)
    {
        if (vertexCount <= 0 || indexCount <= 0)
            return false;

        if (_packedGeometryVertices.Length < vertexCount ||
            _packedGeometryIndices.Length < indexCount)
        {
            return false;
        }
        if (payload is not { HasMeshlets: true })
            return true;

        return _packedMeshletDescriptors.Length >= payload.Meshlets.Length &&
            _packedMeshletTriangleWords.Length >=
                checked((int)(((long)payload.TriangleIndices.Length + 3L) / 4L));
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
