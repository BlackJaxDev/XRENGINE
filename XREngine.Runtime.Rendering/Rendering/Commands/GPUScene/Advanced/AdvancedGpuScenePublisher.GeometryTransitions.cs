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
    // Meshes whose pre-packed entry this publication copied. They are released after it commits.
    private readonly List<XRMesh> _consumedPrepackMeshes = [];
    private long _prepackHitCount;
    private long _prepackMissCount;
    private long _geometryPagePoolRejectCount;
    // The frame of the publication that runs now. Pages that a streamed geometry makes resident
    // at registration count as touched in it.
    private long _geometryStreamingFrame;

    /// <summary>Gets the staged meshes that were copied from a worker-packed entry.</summary>
    internal long PrepackHitCount => _prepackHitCount;

    /// <summary>Gets the staged meshes that were packed on the swap thread.</summary>
    internal long PrepackMissCount => _prepackMissCount;

    /// <summary>
    /// Gets the geometries with geometry pages that the page pool did not have free slots for.
    /// Each one draws from its meshlet arenas instead.
    /// </summary>
    internal long GeometryPagePoolRejectCount => _geometryPagePoolRejectCount;

    /// <summary>Worker-packed canonical geometry that the staging step copies instead of packing.</summary>
    internal AdvancedCanonicalGeometryPrepackCache Prepack { get; } = new();

    /// <summary>Validates that a mesh can be packed into the canonical geometry arenas.</summary>
    internal static bool TryValidateCanonicalGeometrySource(XRMesh mesh)
        => TryValidateCanonicalGeometry(mesh, out _);

    /// <summary>Converts CPU meshlet descriptors into the canonical meshlet descriptor layout.</summary>
    internal static void ConvertMeshletDescriptors(
        ReadOnlySpan<CpuMeshletDescriptor> source,
        Span<AdvancedMeshletDescriptor> destination)
    {
        for (int descriptorIndex = 0; descriptorIndex < source.Length; ++descriptorIndex)
        {
            CpuMeshletDescriptor descriptor = source[descriptorIndex];
            destination[descriptorIndex] = new AdvancedMeshletDescriptor
            {
                BoundsSphere = descriptor.BoundsSphere,
                VertexOffset = descriptor.VertexOffset,
                TriangleByteOffset = descriptor.TriangleOffset,
                VertexCount = descriptor.VertexCount,
                TriangleCount = descriptor.TriangleCount,
                Cone = descriptor.Cone,
                ConeApex = descriptor.ConeApex,
                PackedCone = descriptor.PackedCone,
            };
        }
    }

    /// <summary>Packs meshlet triangle bytes four to a word.</summary>
    internal static void PackMeshletTriangleWords(ReadOnlySpan<byte> triangleBytes, Span<uint> destination)
        => PackTriangleWords(triangleBytes, destination);

    /// <summary>
    /// Returns the number of level cover indices that the publisher appends after the canonical
    /// indices of a geometry: the cover table of a published cluster hierarchy payload, or zero.
    /// The payload validation already checked that the covers address only vertices of the mesh.
    /// </summary>
    private static int ResolveLevelCoverIndexCount(MeshletPayload? payload, bool useMeshlets)
        => useMeshlets && payload is { HasClusterHierarchy: true, HasDeformationOrder: true }
            ? payload.LevelCoverIndices.Length
            : 0;

    /// <summary>
    /// Gets the ready pre-packed entry for the planned geometry. The entry must describe the same
    /// revision, buffers, triangle list, meshlet payload and counts as the mesh does now.
    /// </summary>
    private bool TryGetPrepackedGeometry(
        in AdvancedGpuSceneCommandTransition plan,
        XRMesh mesh,
        out AdvancedCanonicalGeometryPrepackCache.Entry entry)
    {
        if (!Prepack.TryGetReady(mesh, plan.MeshGeometryRevision, out entry))
            return false;
        return entry.VertexCount == plan.MeshVertexCount &&
               entry.IndexCount == plan.MeshIndexCount &&
               ReferenceEquals(entry.Payload, mesh.MeshletPayload);
    }

    /// <summary>Releases the pre-packed entries that the committed publication appended.</summary>
    private void ReleaseConsumedPrepackEntries()
    {
        for (int index = 0; index < _consumedPrepackMeshes.Count; ++index)
            Prepack.Release(_consumedPrepackMeshes[index]);
        _consumedPrepackMeshes.Clear();
    }

    private struct StagedCanonicalGeometry
    {
        public bool Captured;
        public MeshletPayload? SizedPayload;
        public bool SizedWithMeshlets;
        public int VertexOffset;
        public int VertexCount;
        public int IndexOffset;
        public int IndexCount;
        /// <summary>
        /// Level cover indices staged after the <see cref="IndexCount"/> canonical indices of a
        /// skinned cluster hierarchy geometry, or zero.
        /// </summary>
        public int CoverIndexCount;
        public int DescriptorOffset;
        public int DescriptorCount;
        public int MeshletIndexOffset;
        public int MeshletIndexCount;
        public int TriangleWordOffset;
        public int TriangleWordCount;
        public uint ClusterHierarchyWordOffset;
        public uint ClusterLevel0Count;
        /// <summary>The geometry flags of the layout, such as geometry pages.</summary>
        public EAdvancedGeometryFlags GeometryFlags;
        /// <summary>The geometry pages of the layout, or null.</summary>
        public MeshletPageSet? Pages;
        /// <summary>
        /// The index of the first-page word in the staged triangle words of this geometry
        /// (relative to <see cref="TriangleWordOffset"/>). Valid only with <see cref="Pages"/>.
        /// </summary>
        public int FirstPageWordIndex;
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
        ReadOnlySpan<uint> indices = _packedGeometryIndices.AsSpan(
            staged.IndexOffset,
            checked(staged.IndexCount + staged.CoverIndexCount));

        if (staged.DescriptorCount == 0)
            return Database.Scene.Geometry.TryAddStatic(
                vertexBytes,
                indices,
                registration,
                out geometry);

        EAdvancedGeometryFlags geometryFlags = staged.GeometryFlags;
        bool reservedPages = false;
        bool streamedPages = false;
        AdvancedGeometryPagePool.PageRange pageRange = default;
        if (staged.Pages is { } pages)
        {
            reservedPages = TryReserveGeometryPages(pages, in staged, out pageRange, out streamedPages);
            if (!reservedPages)
                geometryFlags &= ~EAdvancedGeometryFlags.StreamedPages;
        }

        registration = registration with
        {
            MeshletCount = checked((uint)staged.DescriptorCount),
            ClusterLevel0Count = staged.ClusterLevel0Count,
            ClusterHierarchyWordOffset = staged.ClusterHierarchyWordOffset,
            LevelCoverIndexCount = checked((uint)staged.CoverIndexCount),
            GeometryFlags = geometryFlags,
        };
        bool added = Database.Scene.Geometry.TryAddMeshletLocal(
            vertexBytes,
            indices,
            _packedMeshletDescriptors.AsSpan(staged.DescriptorOffset, staged.DescriptorCount),
            _packedMeshletVertexIndices.AsSpan(staged.MeshletIndexOffset, staged.MeshletIndexCount),
            _packedMeshletTriangleWords.AsSpan(staged.TriangleWordOffset, staged.TriangleWordCount),
            registration,
            out geometry);
        if (reservedPages)
        {
            if (!added)
                Database.Scene.Geometry.PagePool.Release(pageRange);
            else
            {
                Database.Scene.Geometry.PagePool.Bind(geometry, pageRange);
                // The streaming manager loads and evicts the pages that are not resident yet.
                if (streamedPages)
                    GetGeometryStreamingManager().RegisterGeometry(geometry, pageRange, staged.Pages!, _geometryStreamingFrame);
            }
        }
        return added;
    }

    /// <summary>
    /// Returns the residency manager of the geometry page pool, and creates it on the first call
    /// with the latched upload budget of <see cref="AdvancedVirtualGeometryPolicy"/>.
    /// </summary>
    private MeshletPageStreamingManager GetGeometryStreamingManager()
        => Database.Scene.Geometry.GetOrCreateStreamingManager(new MeshletPageResidencyPolicy
        {
            UploadBytesPerUpdate = AdvancedVirtualGeometryPolicy.GeometryStreamingUploadBytes,
        });

    /// <summary>
    /// Runs the geometry page streaming step of a publication, before the publication captures
    /// the page table. Gives the queued GPU feedback to the streaming manager and runs one
    /// residency update, which loads requested pages into free pool slots and evicts old pages.
    /// Evicted slots retire until the next sealed publication, the first one whose page table does
    /// not name them, is reclaimed. Does nothing while streaming is off or before the first
    /// streamed geometry registers.
    /// </summary>
    private void UpdateGeometryStreaming(ulong frameId)
    {
        _geometryStreamingFrame = (long)frameId;
        if (!AdvancedVirtualGeometryPolicy.GeometryStreamingEnabled)
            return;

        AdvancedGeometryDatabase geometry = Database.Scene.Geometry;
        if (geometry.StreamingManager is not { } manager)
        {
            geometry.StreamingFeedback.Clear();
            return;
        }

        using (RuntimeEngine.Profiler.Start("GpuIndirect.AdvancedPublication.GeometryStreaming"))
        {
            geometry.StreamingFeedback.DrainTo(manager);
            manager.Update(
                _geometryStreamingFrame,
                AdvancedVirtualGeometryPolicy.PixelThreshold,
                Database.NextPublicationSequence);
        }
    }

    /// <summary>
    /// True when the page pool changed its page table after <paramref name="publication"/> was
    /// sealed, so that publication cannot be reused: the next frame must draw with a new capture
    /// of the page table.
    /// </summary>
    private bool HasGeometryPageTableChanged(AdvancedGpuScenePublicationSnapshot publication)
        => publication.GeometryPayloads.PageTableVersion != Database.Scene.Geometry.PagePool.Version;

    /// <summary>
    /// Removes a geometry from the streaming manager when its page range is retired or released.
    /// </summary>
    private void UnregisterStreamedGeometry(AdvancedGpuHandle geometry)
        => Database.Scene.Geometry.StreamingManager?.UnregisterGeometry(geometry);

    /// <summary>
    /// Reserves page table entries and pool slots for the pages of a staged geometry, and writes
    /// the first page into the page block of its staged triangle words, which the geometry add
    /// then copies to the triangle-word arena. With geometry streaming on, a static geometry
    /// (a page set without skinned topology) gets slots only for its root pages, and
    /// <paramref name="streamed"/> is true: the caller then registers it with the streaming
    /// manager, which loads the other pages on request. Otherwise every page gets a slot. Returns
    /// false when the pool does not have enough free slots; the geometry then registers without
    /// <see cref="EAdvancedGeometryFlags.StreamedPages"/> and draws from its meshlet arenas.
    /// </summary>
    private bool TryReserveGeometryPages(
        MeshletPageSet pages,
        in StagedCanonicalGeometry staged,
        out AdvancedGeometryPagePool.PageRange range,
        out bool streamed)
    {
        AdvancedGeometryPagePool pool = Database.Scene.Geometry.PagePool;
        pool.EnsureConfigured(AdvancedVirtualGeometryPolicy.GeometryPagePoolSlotCount);
        streamed = AdvancedVirtualGeometryPolicy.GeometryStreamingEnabled &&
            pages.Table.Kind == EMeshletPageFlags.None &&
            pages.Table.RootPageCount > 0;
        int residentPageCount = streamed ? pages.Table.RootPageCount : pages.PageImages.Length;
        if (!pool.TryReserve(pages, residentPageCount, out range))
        {
            ++_geometryPagePoolRejectCount;
            Debug.RenderingWarningEvery(
                "AdvancedGpuScenePublisher.GeometryPagePoolFull",
                TimeSpan.FromSeconds(5),
                $"Geometry page pool has no room for {residentPageCount} pages (used {pool.UsedSlotCount}/{pool.SlotCount} slots); the geometry draws from its meshlet arenas. Rejected geometries: {_geometryPagePoolRejectCount}.");
            streamed = false;
            return false;
        }

        _packedMeshletTriangleWords[staged.TriangleWordOffset + staged.FirstPageWordIndex] = range.FirstPage;
        return true;
    }

    private bool TryStagePlannedGeometry(out string failure)
    {
        failure = string.Empty;
        _consumedPrepackMeshes.Clear();
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
                // A worker validated and packed a ready entry for this exact revision.
                bool prepacked = TryGetPrepackedGeometry(in plan, mesh, out AdvancedCanonicalGeometryPrepackCache.Entry prepack);
                if (mesh.GeometryRevision != plan.MeshGeometryRevision ||
                    mesh.VertexCount != plan.MeshVertexCount ||
                    mesh.Type != plan.MeshPrimitiveTopology ||
                    mesh.Triangles is not { } triangles ||
                    (long)triangles.Count * 3L != plan.MeshIndexCount ||
                    (!prepacked && !TryValidateCanonicalGeometry(mesh, out _)))
                {
                    failure = $"Command {commandIndex} geometry changed before capture; plannedRevision={plan.MeshGeometryRevision}, currentRevision={mesh.GeometryRevision}.";
                    return false;
                }

                MeshletPayload? payload = mesh.MeshletPayload;
                bool useMeshlets = payload is { HasMeshlets: true } && payload.IsValidatedFor(mesh);
                int vertexCount = mesh.VertexCount;
                int indexCount = checked(triangles.Count * 3);
                AdvancedPublishedMeshletLayout layout = useMeshlets
                    ? AdvancedPublishedMeshletLayout.For(payload!)
                    : default;
                int descriptorCount = layout.DescriptorCount;
                int meshletIndexCount = layout.VertexIndexCount;
                int triangleWordCount = layout.TriangleWordCount;
                int coverIndexCount = ResolveLevelCoverIndexCount(payload, useMeshlets);
                ref StagedCanonicalGeometry sized = ref _stagedGeometry[commandIndex];
                if (!ReferenceEquals(sized.SizedPayload, payload) ||
                    sized.SizedWithMeshlets != useMeshlets ||
                    sized.VertexCount != vertexCount || sized.IndexCount != indexCount ||
                    sized.CoverIndexCount != coverIndexCount ||
                    sized.DescriptorCount != descriptorCount ||
                    sized.MeshletIndexCount != meshletIndexCount ||
                    sized.TriangleWordCount != triangleWordCount ||
                    vertexCount > _packedGeometryVertices.Length - vertexCursor ||
                    indexCount + coverIndexCount > _packedGeometryIndices.Length - indexCursor ||
                    descriptorCount > _packedMeshletDescriptors.Length - descriptorCursor ||
                    meshletIndexCount > _packedMeshletVertexIndices.Length - meshletIndexCursor ||
                    triangleWordCount > _packedMeshletTriangleWords.Length - triangleWordCursor)
                {
                    failure = $"Command {commandIndex} geometry changed after boundary sizing; plannedRevision={plan.MeshGeometryRevision}, currentRevision={mesh.GeometryRevision}.";
                    return false;
                }

                // A worker entry packed before geometry pages were attached has a shorter
                // triangle-word stream; it is packed again here.
                if (prepacked && prepack.UseMeshlets == useMeshlets &&
                    (!useMeshlets || prepack.MeshletTriangleWords.Length == triangleWordCount))
                {
                    prepack.Vertices.AsSpan(0, vertexCount).CopyTo(
                        _packedGeometryVertices.AsSpan(vertexCursor, vertexCount));
                    prepack.Indices.AsSpan(0, indexCount).CopyTo(
                        _packedGeometryIndices.AsSpan(indexCursor, indexCount));
                    if (useMeshlets)
                    {
                        prepack.MeshletDescriptors.AsSpan(0, descriptorCount).CopyTo(
                            _packedMeshletDescriptors.AsSpan(descriptorCursor, descriptorCount));
                        prepack.MeshletVertexIndices.AsSpan(0, meshletIndexCount).CopyTo(
                            _packedMeshletVertexIndices.AsSpan(meshletIndexCursor, meshletIndexCount));
                        prepack.MeshletTriangleWords.AsSpan(0, triangleWordCount).CopyTo(
                            _packedMeshletTriangleWords.AsSpan(triangleWordCursor, triangleWordCount));
                    }
                    _consumedPrepackMeshes.Add(mesh);
                    ++_prepackHitCount;
                }
                else
                {
                    ++_prepackMissCount;
                    AdvancedPackedVertexCodec.Pack(
                        mesh,
                        firstVertex: 0u,
                        _packedGeometryVertices.AsSpan(vertexCursor, vertexCount),
                        firstSourceVertex: 0u);

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
                        layout.Pack(
                            payload!,
                            _packedMeshletDescriptors.AsSpan(descriptorCursor, descriptorCount),
                            _packedMeshletVertexIndices.AsSpan(meshletIndexCursor, meshletIndexCount),
                            _packedMeshletTriangleWords.AsSpan(triangleWordCursor, triangleWordCount));
                    }
                }

                // The level covers follow the canonical indices in the same index arena range.
                if (coverIndexCount != 0)
                    payload!.LevelCoverIndices.AsSpan().CopyTo(
                        _packedGeometryIndices.AsSpan(indexCursor + indexCount, coverIndexCount));

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
                    CoverIndexCount = coverIndexCount,
                    DescriptorOffset = descriptorCursor,
                    DescriptorCount = descriptorCount,
                    MeshletIndexOffset = meshletIndexCursor,
                    MeshletIndexCount = meshletIndexCount,
                    TriangleWordOffset = triangleWordCursor,
                    TriangleWordCount = triangleWordCount,
                    ClusterHierarchyWordOffset = layout.ClusterHierarchyWordOffset,
                    ClusterLevel0Count = layout.ClusterLevel0Count,
                    GeometryFlags = layout.GeometryFlags,
                    Pages = layout.Pages,
                    FirstPageWordIndex = layout.Pages is null ? 0 : layout.FirstPageWordIndex,
                };
                vertexCursor += vertexCount;
                indexCursor += indexCount + coverIndexCount;
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
        ulong indexBytes = (ulong)(staged.IndexCount + staged.CoverIndexCount) * sizeof(uint);
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
                    (!TryGetPrepackedGeometry(in plan, mesh, out _) && !TryValidateCanonicalGeometry(mesh, out _)))
                    return false;

                int vertexCount = mesh.VertexCount;
                int indexCount = checked(triangles.Count * 3);
                MeshletPayload? payload = mesh.MeshletPayload;
                bool useMeshlets = payload is { HasMeshlets: true } && payload.IsValidatedFor(mesh);
                AdvancedPublishedMeshletLayout layout = useMeshlets
                    ? AdvancedPublishedMeshletLayout.For(payload!)
                    : default;
                int descriptorCount = layout.DescriptorCount;
                int meshletIndexCount = layout.VertexIndexCount;
                int triangleWordCount = layout.TriangleWordCount;
                int coverIndexCount = ResolveLevelCoverIndexCount(payload, useMeshlets);
                _stagedGeometry[commandIndex] = new StagedCanonicalGeometry
                {
                    SizedPayload = payload,
                    SizedWithMeshlets = useMeshlets,
                    VertexCount = vertexCount,
                    IndexCount = indexCount,
                    CoverIndexCount = coverIndexCount,
                    DescriptorCount = descriptorCount,
                    MeshletIndexCount = meshletIndexCount,
                    TriangleWordCount = triangleWordCount,
                };
                totalVertexCount = checked(totalVertexCount + vertexCount);
                totalIndexCount = checked(totalIndexCount + indexCount + coverIndexCount);
                totalMeshletDescriptorCount = checked(totalMeshletDescriptorCount + descriptorCount);
                totalMeshletVertexIndexCount = checked(totalMeshletVertexIndexCount + meshletIndexCount);
                totalMeshletTriangleWordCount = checked(totalMeshletTriangleWordCount + triangleWordCount);

                if (!TryAccumulateArenaAppend(
                        ref staticVertexEnd,
                        checked((ulong)vertexCount * vertexStride),
                        vertexStride) ||
                    !TryAccumulateArenaAppend(
                        ref indexEnd,
                        checked((ulong)(indexCount + coverIndexCount) * sizeof(uint)),
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
