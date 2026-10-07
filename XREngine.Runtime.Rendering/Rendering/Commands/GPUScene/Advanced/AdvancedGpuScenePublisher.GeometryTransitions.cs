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
    private uint[] _packedMeshletVertexIndices = [];
    private uint[] _packedMeshletTriangleWords = [];
    private StagedCanonicalGeometry[] _stagedGeometry = [];

    private struct StagedCanonicalGeometry
    {
        public bool Captured;
        public MeshletPayload? SizedPayload;
        public bool SizedWithMeshlets;
        public int VertexOffset;
        public int VertexCount;
        public int IndexOffset;
        public int IndexCount;
        public int DescriptorOffset;
        public int DescriptorCount;
        public int MeshletIndexOffset;
        public int MeshletIndexCount;
        public int TriangleWordOffset;
        public int TriangleWordCount;
    }

    /// <summary>
    /// Reuses triangle validation for the exact geometry revision already admitted
    /// to the canonical scene. In-place source edits must call MarkGeometryChanged,
    /// as required by the mesh's other geometry caches.
    /// </summary>
    private static bool HasValidatedCanonicalGeometry(
        in AdvancedResidentRegistration registration,
        in AdvancedGpuSceneCommandTransition plan)
        => registration.Active &&
           plan.Mesh is { } mesh &&
           ReferenceEquals(registration.TemporalMesh, mesh) &&
           registration.TemporalGeometryRevision == plan.MeshGeometryRevision &&
           registration.TemporalVertexCount == plan.MeshVertexCount &&
           registration.TemporalIndexCount == plan.MeshIndexCount &&
           registration.TemporalPrimitiveTopology == plan.MeshPrimitiveTopology &&
           mesh.VertexCount == plan.MeshVertexCount &&
           mesh.Triangles is { } triangles &&
           (long)triangles.Count * 3 == plan.MeshIndexCount;

    private static bool TryValidateCanonicalGeometry(
        XRMesh? mesh,
        out EAdvancedCanonicalCompatibilityReason reason)
    {
        if (mesh is null || mesh.Type != EPrimitiveType.Triangles)
        {
            reason = EAdvancedCanonicalCompatibilityReason.UnsupportedGeometryTopology;
            return false;
        }

        // The canonical record is packed from the attribute buffers, so they
        // must hold client-side data for every vertex the triangles address.
        int vertexCount = mesh.VertexCount;
        List<IndexTriangle>? triangles = mesh.Triangles;
        if (vertexCount <= 0 ||
            !AdvancedPackedVertexCodec.HasReadableAttributes(mesh) ||
            triangles is null || triangles.Count == 0)
        {
            reason = EAdvancedCanonicalCompatibilityReason.InvalidGeometrySource;
            return false;
        }

        for (int triangleIndex = 0; triangleIndex < triangles.Count; ++triangleIndex)
        {
            IndexTriangle triangle = triangles[triangleIndex];
            if ((uint)triangle.Point0 >= (uint)vertexCount ||
                (uint)triangle.Point1 >= (uint)vertexCount ||
                (uint)triangle.Point2 >= (uint)vertexCount)
            {
                reason = EAdvancedCanonicalCompatibilityReason.InvalidGeometrySource;
                return false;
            }
        }

        reason = EAdvancedCanonicalCompatibilityReason.None;
        return true;
    }

    private bool TryRegisterCanonicalGeometry(
        int commandIndex,
        in BoundsGpu bounds,
        in DrawMetadata command,
        out AdvancedGpuHandle geometry)
    {
        geometry = AdvancedGpuHandle.Invalid;
        ref readonly StagedCanonicalGeometry staged = ref _stagedGeometry[commandIndex];
        if (!staged.Captured)
            return false;

        AdvancedGeometryRegistration registration = AdvancedGeometryRegistration.Create(
            checked((uint)staged.VertexCount),
            checked((uint)staged.IndexCount),
            checked((uint)Unsafe.SizeOf<AdvancedDeformedVertex>()),
            EPrimitiveType.Triangles,
            AdvancedDeformedVertex.CanonicalLayoutId,
            bounds.BoundingSphere,
            bounds.AabbMin,
            bounds.AabbMax,
            command.SubmeshID,
            1u);
        ReadOnlySpan<byte> vertexBytes = MemoryMarshal.AsBytes(
            _packedGeometryVertices.AsSpan(staged.VertexOffset, staged.VertexCount));
        ReadOnlySpan<uint> indices = _packedGeometryIndices.AsSpan(staged.IndexOffset, staged.IndexCount);

        if (staged.DescriptorCount == 0)
            return Database.Scene.Geometry.TryAddStatic(
                vertexBytes,
                indices,
                registration,
                out geometry);

        registration = registration with
        {
            MeshletCount = checked((uint)staged.DescriptorCount),
        };
        return Database.Scene.Geometry.TryAddMeshletLocal(
            vertexBytes,
            indices,
            _packedMeshletDescriptors.AsSpan(staged.DescriptorOffset, staged.DescriptorCount),
            _packedMeshletVertexIndices.AsSpan(staged.MeshletIndexOffset, staged.MeshletIndexCount),
            _packedMeshletTriangleWords.AsSpan(staged.TriangleWordOffset, staged.TriangleWordCount),
            registration,
            out geometry);
    }

    private bool TryStagePlannedGeometry(out string failure)
    {
        failure = string.Empty;
        int vertexCursor = 0;
        int indexCursor = 0;
        int descriptorCursor = 0;
        int meshletIndexCursor = 0;
        int triangleWordCursor = 0;

        for (int commandIndex = 0; commandIndex < _plannedCommandCount; ++commandIndex)
        {
            ref readonly AdvancedGpuSceneCommandTransition plan = ref _plannedCommands[commandIndex];
            if (!plan.Supported || !RequiresGeometryAppend(in plan))
                continue;

            XRMesh? mesh = plan.Mesh;
            if (mesh is null)
            {
                failure = $"Command {commandIndex} lost its geometry source before capture.";
                return false;
            }

            XRMesh.BufferCollection buffers = mesh.Buffers;
            if (!buffers.TryAcquireGeometryReadScope(mesh, out XRMesh.BufferCollection.GeometryReadScope scope))
            {
                failure = $"Command {commandIndex} geometry source is changing or retiring.";
                return false;
            }

            using (scope)
            {
                if (mesh.GeometryRevision != plan.MeshGeometryRevision ||
                    mesh.VertexCount != plan.MeshVertexCount ||
                    mesh.Type != plan.MeshPrimitiveTopology ||
                    mesh.Triangles is not { } triangles ||
                    (long)triangles.Count * 3L != plan.MeshIndexCount ||
                    !TryValidateCanonicalGeometry(mesh, out _))
                {
                    failure = $"Command {commandIndex} geometry changed before capture; plannedRevision={plan.MeshGeometryRevision}, currentRevision={mesh.GeometryRevision}.";
                    return false;
                }

                MeshletPayload? payload = mesh.MeshletPayload;
                bool useMeshlets = payload is { HasMeshlets: true } && payload.IsValidatedFor(mesh);
                int vertexCount = mesh.VertexCount;
                int indexCount = checked(triangles.Count * 3);
                int descriptorCount = useMeshlets ? payload!.Meshlets.Length : 0;
                int meshletIndexCount = useMeshlets ? payload!.VertexIndices.Length : 0;
                int triangleWordCount = useMeshlets
                    ? checked((int)(((long)payload!.TriangleIndices.Length + 3L) / 4L))
                    : 0;
                ref StagedCanonicalGeometry sized = ref _stagedGeometry[commandIndex];
                if (!ReferenceEquals(sized.SizedPayload, payload) ||
                    sized.SizedWithMeshlets != useMeshlets ||
                    sized.VertexCount != vertexCount || sized.IndexCount != indexCount ||
                    sized.DescriptorCount != descriptorCount ||
                    sized.MeshletIndexCount != meshletIndexCount ||
                    sized.TriangleWordCount != triangleWordCount ||
                    vertexCount > _packedGeometryVertices.Length - vertexCursor ||
                    indexCount > _packedGeometryIndices.Length - indexCursor ||
                    descriptorCount > _packedMeshletDescriptors.Length - descriptorCursor ||
                    meshletIndexCount > _packedMeshletVertexIndices.Length - meshletIndexCursor ||
                    triangleWordCount > _packedMeshletTriangleWords.Length - triangleWordCursor)
                {
                    failure = $"Command {commandIndex} geometry changed after boundary sizing; plannedRevision={plan.MeshGeometryRevision}, currentRevision={mesh.GeometryRevision}.";
                    return false;
                }

                for (uint vertexIndex = 0; vertexIndex < (uint)vertexCount; ++vertexIndex)
                    _packedGeometryVertices[vertexCursor + checked((int)vertexIndex)] = AdvancedPackedVertexCodec.Pack(
                        mesh, vertexIndex, vertexIndex);

                int destinationIndex = indexCursor;
                for (int triangleIndex = 0; triangleIndex < triangles.Count; ++triangleIndex)
                {
                    IndexTriangle triangle = triangles[triangleIndex];
                    _packedGeometryIndices[destinationIndex++] = checked((uint)triangle.Point0);
                    _packedGeometryIndices[destinationIndex++] = checked((uint)triangle.Point1);
                    _packedGeometryIndices[destinationIndex++] = checked((uint)triangle.Point2);
                }

                if (useMeshlets)
                {
                    for (int descriptorIndex = 0; descriptorIndex < descriptorCount; ++descriptorIndex)
                    {
                        CpuMeshletDescriptor source = payload!.Meshlets[descriptorIndex];
                        _packedMeshletDescriptors[descriptorCursor + descriptorIndex] = new AdvancedMeshletDescriptor
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
                    payload!.VertexIndices.AsSpan().CopyTo(
                        _packedMeshletVertexIndices.AsSpan(meshletIndexCursor, meshletIndexCount));
                    PackTriangleWords(
                        payload.TriangleIndices.AsSpan(),
                        _packedMeshletTriangleWords.AsSpan(triangleWordCursor, triangleWordCount));
                }

                if (!ReferenceEquals(mesh.Buffers, buffers) ||
                    !ReferenceEquals(mesh.MeshletPayload, payload) ||
                    mesh.GeometryRevision != plan.MeshGeometryRevision ||
                    mesh.VertexCount != vertexCount ||
                    mesh.Type != plan.MeshPrimitiveTopology ||
                    !ReferenceEquals(mesh.Triangles, triangles) ||
                    triangles.Count * 3L != indexCount ||
                    mesh.IsDestroyQueued || mesh.IsDestroyed)
                {
                    failure = $"Command {commandIndex} geometry changed during capture; plannedRevision={plan.MeshGeometryRevision}, currentRevision={mesh.GeometryRevision}.";
                    return false;
                }

                _stagedGeometry[commandIndex] = new StagedCanonicalGeometry
                {
                    Captured = true,
                    VertexOffset = vertexCursor,
                    VertexCount = vertexCount,
                    IndexOffset = indexCursor,
                    IndexCount = indexCount,
                    DescriptorOffset = descriptorCursor,
                    DescriptorCount = descriptorCount,
                    MeshletIndexOffset = meshletIndexCursor,
                    MeshletIndexCount = meshletIndexCount,
                    TriangleWordOffset = triangleWordCursor,
                    TriangleWordCount = triangleWordCount,
                };
                vertexCursor += vertexCount;
                indexCursor += indexCount;
                descriptorCursor += descriptorCount;
                meshletIndexCursor += meshletIndexCount;
                triangleWordCursor += triangleWordCount;
            }
        }

        return true;
    }

    private string DescribeCanonicalGeometryRegistrationFailure(int commandIndex, long plannedRevision)
    {
        AdvancedGeometryDatabase geometry = Database.Scene.Geometry;
        ref readonly StagedCanonicalGeometry staged = ref _stagedGeometry[commandIndex];
        string state = $"plannedRevision={plannedRevision}, captured={staged.Captured}, " +
            $"vertices={staged.VertexCount}/{_packedGeometryVertices.Length}, indices={staged.IndexCount}/{_packedGeometryIndices.Length}, " +
            $"meshletDescriptors={staged.DescriptorCount}/{_packedMeshletDescriptors.Length}, " +
            $"meshletIndices={staged.MeshletIndexCount}/{_packedMeshletVertexIndices.Length}, " +
            $"meshletTriangleWords={staged.TriangleWordCount}/{_packedMeshletTriangleWords.Length}, " +
            $"geometryRows={geometry.Records.Count}+{geometry.Records.RetiredCount}/{geometry.Records.Capacity}, " +
            $"vertexBytes={geometry.StaticVertexArena.CountBytes}/{geometry.StaticVertexArena.CapacityBytes}, " +
            $"indexBytes={geometry.IndexArena.CountBytes}/{geometry.IndexArena.CapacityBytes}, " +
            $"descriptorBytes={geometry.MeshletDescriptorArena.CountBytes}/{geometry.MeshletDescriptorArena.CapacityBytes}, " +
            $"meshletIndexBytes={geometry.MeshletVertexIndexArena.CountBytes}/{geometry.MeshletVertexIndexArena.CapacityBytes}, " +
            $"triangleWordBytes={geometry.MeshletTriangleWordArena.CountBytes}/{geometry.MeshletTriangleWordArena.CapacityBytes}";

        if (!staged.Captured)
            return $"Geometry capture was not present; {state}";
        if (geometry.Records.AvailableAdditions < 1 || geometry.Records.AvailablePublicationDeltas < 1)
            return $"Geometry record add has no capacity or publication delta; {state}";

        ulong vertexBytes = (ulong)staged.VertexCount * (uint)Unsafe.SizeOf<AdvancedDeformedVertex>();
        ulong indexBytes = (ulong)staged.IndexCount * sizeof(uint);
        if (vertexBytes > uint.MaxValue ||
            !geometry.StaticVertexArena.CanAppend((uint)vertexBytes, (uint)Unsafe.SizeOf<AdvancedDeformedVertex>()))
            return $"Static vertex arena rejected the append of {vertexBytes} bytes; {state}";
        if (indexBytes > uint.MaxValue ||
            !geometry.IndexArena.CanAppend((uint)indexBytes, sizeof(uint)))
            return $"Geometry index arena rejected the append of {indexBytes} bytes; {state}";

        if (staged.DescriptorCount != 0)
        {
            ulong descriptorBytes = (ulong)staged.DescriptorCount * (uint)Unsafe.SizeOf<AdvancedMeshletDescriptor>();
            ulong meshletIndexBytes = (ulong)staged.MeshletIndexCount * sizeof(uint);
            ulong triangleBytes = (ulong)staged.TriangleWordCount * sizeof(uint);
            if (descriptorBytes > uint.MaxValue ||
                !geometry.MeshletDescriptorArena.CanAppend((uint)descriptorBytes, (uint)Unsafe.SizeOf<AdvancedMeshletDescriptor>()))
                return $"Meshlet descriptor arena rejected the append of {descriptorBytes} bytes; {state}";
            if (meshletIndexBytes > uint.MaxValue ||
                !geometry.MeshletVertexIndexArena.CanAppend((uint)meshletIndexBytes, sizeof(uint)))
                return $"Meshlet vertex-index arena rejected the append of {meshletIndexBytes} bytes; {state}";
            if (triangleBytes > uint.MaxValue ||
                !geometry.MeshletTriangleWordArena.CanAppend((uint)triangleBytes, sizeof(uint)))
                return $"Meshlet triangle-word arena rejected the append of {triangleBytes} bytes; {state}";
        }

        return $"Geometry add failed after staged record and arena checks; {state}";
    }

    /// <summary>
    /// Preflights every immutable geometry append while growth is still legal.
    /// The publication transaction that follows may mutate only fixed storage.
    /// </summary>
    private bool TryEnsurePlannedGeometryBoundaryCapacity(
        out AdvancedGeometryCompactionPlan? compactionPlan)
    {
        compactionPlan = null;
        AdvancedGeometryDatabase geometry = Database.Scene.Geometry;
        ulong staticVertexEnd = geometry.StaticVertexArena.CountBytes;
        ulong indexEnd = geometry.IndexArena.CountBytes;
        ulong meshletDescriptorEnd = geometry.MeshletDescriptorArena.CountBytes;
        ulong meshletVertexIndexEnd = geometry.MeshletVertexIndexArena.CountBytes;
        ulong meshletTriangleWordEnd = geometry.MeshletTriangleWordArena.CountBytes;
        int totalVertexCount = 0;
        int totalIndexCount = 0;
        int totalMeshletDescriptorCount = 0;
        int totalMeshletVertexIndexCount = 0;
        int totalMeshletTriangleWordCount = 0;
        uint vertexStride = checked((uint)Unsafe.SizeOf<AdvancedDeformedVertex>());
        uint meshletDescriptorStride =
            checked((uint)Unsafe.SizeOf<AdvancedMeshletDescriptor>());
        EnsureCapacity(ref _stagedGeometry, _plannedCommandCount);
        Array.Clear(_stagedGeometry, 0, _plannedCommandCount);

        for (int commandIndex = 0;
             commandIndex < _plannedCommandCount;
             ++commandIndex)
        {
            ref readonly AdvancedGpuSceneCommandTransition plan =
                ref _plannedCommands[commandIndex];
            if (!plan.Supported || !RequiresGeometryAppend(in plan))
                continue;
            XRMesh? mesh = plan.Mesh;
            if (mesh is null)
                return false;
            XRMesh.BufferCollection buffers = mesh.Buffers;
            if (!buffers.TryAcquireGeometryReadScope(mesh, out XRMesh.BufferCollection.GeometryReadScope scope))
                return false;
            using (scope)
            {
                if (mesh.GeometryRevision != plan.MeshGeometryRevision ||
                    mesh.VertexCount != plan.MeshVertexCount ||
                    mesh.Type != plan.MeshPrimitiveTopology ||
                    mesh.Triangles is not { } triangles ||
                    (long)triangles.Count * 3L != plan.MeshIndexCount ||
                    !TryValidateCanonicalGeometry(mesh, out _))
                    return false;

                int vertexCount = mesh.VertexCount;
                int indexCount = checked(triangles.Count * 3);
                MeshletPayload? payload = mesh.MeshletPayload;
                bool useMeshlets = payload is { HasMeshlets: true } && payload.IsValidatedFor(mesh);
                int descriptorCount = useMeshlets ? payload!.Meshlets.Length : 0;
                int meshletIndexCount = useMeshlets ? payload!.VertexIndices.Length : 0;
                int triangleWordCount = useMeshlets
                    ? checked((int)(((long)payload!.TriangleIndices.Length + 3L) / 4L))
                    : 0;
                _stagedGeometry[commandIndex] = new StagedCanonicalGeometry
                {
                    SizedPayload = payload,
                    SizedWithMeshlets = useMeshlets,
                    VertexCount = vertexCount,
                    IndexCount = indexCount,
                    DescriptorCount = descriptorCount,
                    MeshletIndexCount = meshletIndexCount,
                    TriangleWordCount = triangleWordCount,
                };
                totalVertexCount = checked(totalVertexCount + vertexCount);
                totalIndexCount = checked(totalIndexCount + indexCount);
                totalMeshletDescriptorCount = checked(totalMeshletDescriptorCount + descriptorCount);
                totalMeshletVertexIndexCount = checked(totalMeshletVertexIndexCount + meshletIndexCount);
                totalMeshletTriangleWordCount = checked(totalMeshletTriangleWordCount + triangleWordCount);

                if (!TryAccumulateArenaAppend(
                        ref staticVertexEnd,
                        checked((ulong)vertexCount * vertexStride),
                        vertexStride) ||
                    !TryAccumulateArenaAppend(
                        ref indexEnd,
                        checked((ulong)indexCount * sizeof(uint)),
                        sizeof(uint)))
                    return false;

                if (useMeshlets &&
                    (!TryAccumulateArenaAppend(
                        ref meshletDescriptorEnd,
                        checked((ulong)descriptorCount * meshletDescriptorStride),
                        meshletDescriptorStride) ||
                     !TryAccumulateArenaAppend(
                        ref meshletVertexIndexEnd,
                        checked((ulong)meshletIndexCount * sizeof(uint)),
                        sizeof(uint)) ||
                     !TryAccumulateArenaAppend(
                        ref meshletTriangleWordEnd,
                        checked((ulong)triangleWordCount * sizeof(uint)),
                        sizeof(uint))))
                    return false;

                if (mesh.GeometryRevision != plan.MeshGeometryRevision ||
                    !ReferenceEquals(mesh.Buffers, buffers) ||
                    !ReferenceEquals(mesh.MeshletPayload, payload) ||
                    mesh.VertexCount != vertexCount ||
                    mesh.Type != plan.MeshPrimitiveTopology ||
                    !ReferenceEquals(mesh.Triangles, triangles) ||
                    triangles.Count * 3L != indexCount ||
                    mesh.IsDestroyQueued || mesh.IsDestroyed)
                    return false;
            }
        }

        EnsureCapacity(ref _packedGeometryVertices, totalVertexCount);
        EnsureCapacity(ref _packedGeometryIndices, totalIndexCount);
        EnsureCapacity(ref _packedMeshletDescriptors, totalMeshletDescriptorCount);
        EnsureCapacity(ref _packedMeshletVertexIndices, totalMeshletVertexIndexCount);
        EnsureCapacity(ref _packedMeshletTriangleWords, totalMeshletTriangleWordCount);

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

        AdvancedGpuSceneCapacityProfile currentCapacities = Database.Capacities.Scene;
        AdvancedGpuSceneCapacityProfile sceneCapacities =
            currentCapacities with
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

        if (Database.TryEstimateGeometryCompactionAtFrameBoundary(
                out AdvancedGeometryCompactionEstimate estimate) &&
            estimate.ReclaimedBytes != 0u)
        {
            AdvancedGpuSceneCapacityProfile compactedCapacities = currentCapacities with
            {
                StaticVertexBytes = GetArenaBoundaryCapacity(
                    geometry.StaticVertexArena.CapacityBytes,
                    GetCompactedRequiredEnd(
                        estimate.StaticVertices,
                        geometry.StaticVertexArena.CountBytes,
                        requiredStaticVertexBytes,
                        vertexStride)),
                IndexBytes = GetArenaBoundaryCapacity(
                    geometry.IndexArena.CapacityBytes,
                    GetCompactedRequiredEnd(
                        estimate.Indices,
                        geometry.IndexArena.CountBytes,
                        requiredIndexBytes,
                        sizeof(uint))),
                MeshletDescriptorBytes = GetArenaBoundaryCapacity(
                    geometry.MeshletDescriptorArena.CapacityBytes,
                    GetCompactedRequiredEnd(
                        estimate.MeshletDescriptors,
                        geometry.MeshletDescriptorArena.CountBytes,
                        requiredMeshletDescriptorBytes,
                        meshletDescriptorStride)),
                MeshletVertexIndexBytes = GetArenaBoundaryCapacity(
                    geometry.MeshletVertexIndexArena.CapacityBytes,
                    GetCompactedRequiredEnd(
                        estimate.MeshletVertexIndices,
                        geometry.MeshletVertexIndexArena.CountBytes,
                        requiredMeshletVertexIndexBytes,
                        sizeof(uint))),
                MeshletTriangleWordBytes = GetArenaBoundaryCapacity(
                    geometry.MeshletTriangleWordArena.CapacityBytes,
                    GetCompactedRequiredEnd(
                        estimate.MeshletTriangleWords,
                        geometry.MeshletTriangleWordArena.CountBytes,
                        requiredMeshletTriangleWordBytes,
                        sizeof(uint))),
            };
            if (ReducesGeometryCapacity(in compactedCapacities, in sceneCapacities) &&
                Database.TryStageGeometryCompactionAtFrameBoundary(
                    in compactedCapacities,
                    out AdvancedGeometryCompactionPlan staged) &&
                CanFitPlannedGeometryAfterCompaction(
                    staged,
                    geometry,
                    requiredStaticVertexBytes,
                    requiredIndexBytes,
                    requiredMeshletDescriptorBytes,
                    requiredMeshletVertexIndexBytes,
                    requiredMeshletTriangleWordBytes,
                    vertexStride,
                    meshletDescriptorStride))
            {
                compactionPlan = staged;
                return true;
            }
        }

        return Database.TryGrowGeometryArenasAtFrameBoundary(
            in sceneCapacities);
    }

    private static uint GetCompactedRequiredEnd(
        uint compactedCount,
        uint originalCount,
        uint originalRequiredEnd,
        uint elementStride)
    {
        if (originalRequiredEnd <= originalCount)
            return compactedCount;

        uint originalAlignedCount = AlignUp(originalCount, elementStride);
        uint payloadBytes = checked(originalRequiredEnd - originalAlignedCount);
        return checked(AlignUp(compactedCount, elementStride) + payloadBytes);
    }

    private static bool ReducesGeometryCapacity(
        in AdvancedGpuSceneCapacityProfile compacted,
        in AdvancedGpuSceneCapacityProfile ordinary)
        => compacted.StaticVertexBytes < ordinary.StaticVertexBytes ||
           compacted.IndexBytes < ordinary.IndexBytes ||
           compacted.MeshletDescriptorBytes < ordinary.MeshletDescriptorBytes ||
           compacted.MeshletVertexIndexBytes < ordinary.MeshletVertexIndexBytes ||
           compacted.MeshletTriangleWordBytes < ordinary.MeshletTriangleWordBytes;

    private static bool CanFitPlannedGeometryAfterCompaction(
        AdvancedGeometryCompactionPlan plan,
        AdvancedGeometryDatabase geometry,
        uint requiredStaticVertexBytes,
        uint requiredIndexBytes,
        uint requiredMeshletDescriptorBytes,
        uint requiredMeshletVertexIndexBytes,
        uint requiredMeshletTriangleWordBytes,
        uint vertexStride,
        uint meshletDescriptorStride)
        => CanAppendPlannedBytes(
            plan.StaticVertices,
            geometry.StaticVertexArena.CountBytes,
            requiredStaticVertexBytes,
            vertexStride) &&
           CanAppendPlannedBytes(
               plan.Indices,
               geometry.IndexArena.CountBytes,
               requiredIndexBytes,
               sizeof(uint)) &&
           CanAppendPlannedBytes(
               plan.MeshletDescriptors,
               geometry.MeshletDescriptorArena.CountBytes,
               requiredMeshletDescriptorBytes,
               meshletDescriptorStride) &&
           CanAppendPlannedBytes(
               plan.MeshletVertexIndices,
               geometry.MeshletVertexIndexArena.CountBytes,
               requiredMeshletVertexIndexBytes,
               sizeof(uint)) &&
           CanAppendPlannedBytes(
               plan.MeshletTriangleWords,
               geometry.MeshletTriangleWordArena.CountBytes,
               requiredMeshletTriangleWordBytes,
               sizeof(uint));

    private static bool CanAppendPlannedBytes(
        AdvancedImmutableByteArena successor,
        uint originalCount,
        uint originalRequiredEnd,
        uint elementStride)
    {
        if (originalRequiredEnd <= originalCount)
            return true;

        uint originalAlignedCount = AlignUp(originalCount, elementStride);
        if (originalRequiredEnd < originalAlignedCount)
            return false;
        return successor.CanAppend(
            originalRequiredEnd - originalAlignedCount,
            elementStride);
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

    private static uint AlignUp(uint value, uint alignment)
    {
        uint remainder = value % alignment;
        return remainder == 0u
            ? value
            : checked(value + alignment - remainder);
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
