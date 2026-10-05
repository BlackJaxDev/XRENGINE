using XREngine.Rendering.Diagnostics;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using Silk.NET.Vulkan;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Bounded current-frame stable-bin stream. It is built only after context
/// coalescing and ingress finalization, then frozen before recording. This
/// separates retained topology from frame-local visibility, payload, and late
/// resource uses.
/// </summary>
/// <remarks>
/// The declared capacities are admission limits. Storage starts at
/// <see cref="InitialRowCapacity"/> rows and doubles on a new high-water mark,
/// up to those limits, while the stream is rebuilt by its owning thread.
/// Record/header columns grow only while the stream is mutable, so a frozen
/// stream never replaces an array a recording worker reads. Payload-indexed
/// scratch columns are rewritten by each build step that uses them and grow
/// in that step.
/// </remarks>
internal sealed class VulkanPreparedStableBinStream
{
    /// <summary>Rows allocated per column when a stream is created.</summary>
    internal const int InitialRowCapacity = 256;

    private readonly int _maximumRowCapacity;
    private readonly int _maximumResourceUseCapacity;
    // Record/header-indexed columns; all share one length (see EnsureRowCapacity).
    private VulkanPreparedStableBinRecord[] _records;
    private VulkanPreparedStableBinHeader[] _headers;
    private VulkanSealedBinSubmissionPlan?[] _sealScratchPlans;
    private byte[] _sealScratchPlanAssigned;
    private AdvancedIndirectRange[] _sealScratchRanges;
    private VulkanTemplateResourceManifest[] _manifestTemplates;
    private VulkanTemplateResourceManifest?[] _visibilityAtlasManifests;
    private VulkanResidentDrawDependency[] _visibilityManifestResourceSlab;
    private VulkanTemplateNativeResourceUse[] _visibilityManifestNativeUseSlab;
    private VulkanBinResourceManifest?[] _visibilityManifestViews;
    private VulkanResidentDrawTemplate?[] _retainedTemplates;
    // Payload-, draw- or ingress-indexed columns; all share one length (see
    // EnsurePayloadCapacity).
    private int[] _payloadIndexByIngressScratch;
    private int[] _rangeIndexByPayloadScratch;
    private AdvancedVisibilityPayload[] _visibilityRasterPayloads;
    private byte[] _visibilityRasterPayloadWrites;
    private AdvancedPreparedDrawDeformationRecord[] _deformationOverlay;
    private byte[] _deformationOverlayWrites;
    private FrameOpResourceUse[] _lateResourceUses;
    private readonly VulkanSealedBinExceptionSnapshot _sealedExceptions;
    private readonly VulkanCpuIndirectParityArtifact _cpuIndirectParity;
    private readonly VulkanBinOrderedExceptionStream _exceptions;
    // One prepared raster pipeline per (coverage, meshlet, cull class) for the
    // duration of a single TryPrepareVisibilityRasterPipelines call: those are
    // the only header inputs the visibility pipeline depends on besides the
    // call's one target closure.
    private const int RasterPipelineScratchCapacity = 8;
    private readonly VulkanVisibilityRasterPipeline[] _rasterPipelineScratch =
        new VulkanVisibilityRasterPipeline[RasterPipelineScratchCapacity];
    private readonly bool[] _rasterPipelineScratchValid =
        new bool[RasterPipelineScratchCapacity];
    // Freeze ordering sorts these compact keys and applies the resulting
    // permutation to the records in place; both are record-indexed columns.
    private VulkanPreparedStableBinSortKey[] _freezeSortKeys;
    private int[] _freezeSortOrder;
    private int _recordCount;
    private int _lateResourceUseCount;
    private int _headerCount;
    private bool _frozen;
    private bool _submissionPlansSealed;
    private int _retainedTemplateCount;
    private int _deformationOverlayCount;
    private VulkanAdvancedVisibilityGeometrySlices _visibilityGeometrySources;
    internal VulkanPreparedStableBinStream(int capacity, int resourceUseCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfNegative(resourceUseCapacity);
        _maximumRowCapacity = capacity;
        _maximumResourceUseCapacity = resourceUseCapacity;
        int rows = Math.Min(capacity, InitialRowCapacity);
        _records = new VulkanPreparedStableBinRecord[rows];
        _headers = new VulkanPreparedStableBinHeader[rows];
        // Submission plans are created on first use per header index (see
        // RentSealScratchPlan), so a stream holds plans only up to its high-water
        // header count instead of one per row.
        _sealScratchPlans = new VulkanSealedBinSubmissionPlan?[rows];
        _sealScratchPlanAssigned = new byte[rows];
        _sealScratchRanges = new AdvancedIndirectRange[rows];
        _manifestTemplates = new VulkanTemplateResourceManifest[rows];
        _visibilityAtlasManifests = new VulkanTemplateResourceManifest?[rows];
        _visibilityManifestResourceSlab = new VulkanResidentDrawDependency[checked(rows * 3)];
        _visibilityManifestNativeUseSlab = new VulkanTemplateNativeResourceUse[checked(rows * 2)];
        // Per-row manifests are created on first use (see VisibilityAtlasManifest
        // and VisibilityManifestView), like the seal scratch plans.
        _visibilityManifestViews = new VulkanBinResourceManifest?[rows];
        _retainedTemplates = new VulkanResidentDrawTemplate?[rows];
        _freezeSortKeys = new VulkanPreparedStableBinSortKey[rows];
        _freezeSortOrder = new int[rows];
        _payloadIndexByIngressScratch = new int[rows];
        _rangeIndexByPayloadScratch = new int[rows];
        _visibilityRasterPayloads = new AdvancedVisibilityPayload[rows];
        _visibilityRasterPayloadWrites = new byte[rows];
        _deformationOverlay = new AdvancedPreparedDrawDeformationRecord[rows];
        _deformationOverlayWrites = new byte[rows];
        _lateResourceUses = new FrameOpResourceUse[
            capacity == 0 ? resourceUseCapacity : Math.Min(resourceUseCapacity, rows * (resourceUseCapacity / capacity))];
        _sealedExceptions = new VulkanSealedBinExceptionSnapshot(capacity);
        _cpuIndirectParity = new VulkanCpuIndirectParityArtifact(capacity);
        _exceptions = new VulkanBinOrderedExceptionStream(capacity);
    }

    /// <summary>
    /// Grows every record/header-indexed column to at least
    /// <paramref name="rows"/> rows, doubling up to the declared capacity.
    /// Only a mutable stream grows, so frozen readers never observe a replaced
    /// array. Stream-owned manifest views are rebound to the grown slabs.
    /// </summary>
    private bool EnsureRowCapacity(int rows)
    {
        if (rows <= _records.Length)
            return true;
        if (_frozen || rows > _maximumRowCapacity)
            return false;

        int capacity = Math.Min(_maximumRowCapacity, Math.Max(rows, _records.Length * 2));
        Array.Resize(ref _records, capacity);
        Array.Resize(ref _headers, capacity);
        Array.Resize(ref _sealScratchPlans, capacity);
        Array.Resize(ref _sealScratchPlanAssigned, capacity);
        Array.Resize(ref _sealScratchRanges, capacity);
        Array.Resize(ref _manifestTemplates, capacity);
        Array.Resize(ref _visibilityAtlasManifests, capacity);
        Array.Resize(ref _visibilityManifestViews, capacity);
        Array.Resize(ref _retainedTemplates, capacity);
        Array.Resize(ref _freezeSortKeys, capacity);
        Array.Resize(ref _freezeSortOrder, capacity);
        Array.Resize(ref _visibilityManifestResourceSlab, checked(capacity * 3));
        Array.Resize(ref _visibilityManifestNativeUseSlab, checked(capacity * 2));
        for (int index = 0; index < _visibilityManifestViews.Length; ++index)
            _visibilityManifestViews[index]?.RebindStreamSlabs(
                _visibilityManifestResourceSlab,
                _visibilityManifestNativeUseSlab);
        return true;
    }

    /// <summary>
    /// Grows the payload-, draw- and ingress-indexed scratch columns to at
    /// least <paramref name="length"/> entries, up to the declared capacity.
    /// New index-map entries read as unmapped (-1).
    /// </summary>
    private bool EnsurePayloadCapacity(int length)
    {
        int current = _deformationOverlay.Length;
        if (length <= current)
            return true;
        if (length > _maximumRowCapacity)
            return false;

        int capacity = Math.Min(_maximumRowCapacity, Math.Max(length, current * 2));
        Array.Resize(ref _payloadIndexByIngressScratch, capacity);
        Array.Resize(ref _rangeIndexByPayloadScratch, capacity);
        _payloadIndexByIngressScratch.AsSpan(current).Fill(-1);
        _rangeIndexByPayloadScratch.AsSpan(current).Fill(-1);
        Array.Resize(ref _visibilityRasterPayloads, capacity);
        Array.Resize(ref _visibilityRasterPayloadWrites, capacity);
        Array.Resize(ref _deformationOverlay, capacity);
        Array.Resize(ref _deformationOverlayWrites, capacity);
        return true;
    }

    /// <summary>
    /// Grows the late resource-use column to hold <paramref name="count"/>
    /// entries, doubling up to the declared capacity, while the stream is mutable.
    /// </summary>
    private bool EnsureLateResourceUseCapacity(int count)
    {
        if (count <= _lateResourceUses.Length)
            return true;
        if (_frozen || count > _maximumResourceUseCapacity)
            return false;

        Array.Resize(
            ref _lateResourceUses,
            Math.Min(_maximumResourceUseCapacity, Math.Max(count, _lateResourceUses.Length * 2)));
        return true;
    }

    /// <summary>
    /// Returns the reusable submission plan for a header slot, creating it the
    /// first time that slot is sealed. Creation happens once per new high-water
    /// header count; later frames reuse the same instance.
    /// </summary>
    private VulkanSealedBinSubmissionPlan RentSealScratchPlan(int headerIndex)
        => _sealScratchPlans[headerIndex] ??= new VulkanSealedBinSubmissionPlan();

    /// <summary>Returns the reusable visibility atlas manifest for a record row, creating it on first use.</summary>
    private VulkanTemplateResourceManifest VisibilityAtlasManifest(int recordIndex)
        => _visibilityAtlasManifests[recordIndex] ??= new VulkanTemplateResourceManifest(3, 3);

    /// <summary>Returns the stream-owned manifest view for a header row, creating it on first use.</summary>
    private VulkanBinResourceManifest VisibilityManifestView(int headerIndex)
        => _visibilityManifestViews[headerIndex] ??= VulkanBinResourceManifest.CreateStreamOwned(
            _visibilityManifestResourceSlab,
            _visibilityManifestNativeUseSlab);

    internal int RecordCount => _recordCount;
    /// <summary>Admission limit for records; storage may currently be smaller.</summary>
    internal int RecordCapacity => _maximumRowCapacity;
    internal int LateResourceUseCount => _lateResourceUseCount;
    /// <summary>Admission limit for late resource uses; storage may currently be smaller.</summary>
    internal int LateResourceUseCapacity => _maximumResourceUseCapacity;
    internal int HeaderCount => _headerCount;
    /// <summary>Admission limit for headers; storage may currently be smaller.</summary>
    internal int HeaderCapacity => _maximumRowCapacity;
    internal int OrderedExceptionCount => _exceptions.Count;
    internal int OrderedExceptionCapacity => _exceptions.Capacity;
    internal bool IsFrozen => _frozen;
    internal bool HasSealedSubmissionPlans => _submissionPlansSealed;
    internal ReadOnlySpan<VulkanPreparedStableBinRecord> Records
        => _records.AsSpan(0, _recordCount);
    internal ReadOnlySpan<VulkanPreparedStableBinHeader> Headers
        => _headers.AsSpan(0, _headerCount);
    internal ReadOnlySpan<FrameOpResourceUse> LateResourceUses
        => _lateResourceUses.AsSpan(0, _lateResourceUseCount);
    internal ReadOnlySpan<VulkanBinOrderedException> OrderedExceptions
        => _exceptions.Entries;
    internal ReadOnlySpan<AdvancedPreparedDrawDeformationRecord> DeformationOverlay
        => _deformationOverlay.AsSpan(0, _deformationOverlayCount);
    internal VulkanAdvancedVisibilityGeometrySlices VisibilityGeometrySources
        => _visibilityGeometrySources;
    /// <summary>
    /// Current-frame CPU-direct/CPU-indirect evidence. This artifact is never
    /// consulted by strategy resolution or native command recording.
    /// </summary>
    internal VulkanCpuIndirectParityArtifact CpuIndirectParity
        => _cpuIndirectParity;

    /// <summary>
    /// Materializes visibility geometry directly from the retained canonical
    /// advanced-scene publication. Legacy traversal supplies enumeration only;
    /// packed vertex/index arenas and canonical payload identities are the
    /// frozen Vulkan recording authority.
    /// </summary>
    internal bool TryBuildVisibilityGeometryStream(
        VulkanResourceRuntime resources,
        ReadOnlySpan<AdvancedVisibilityPayload> payloads,
        ReadOnlySpan<AdvancedDeformedArenaSlice> deformationSlices,
        in AdvancedGpuDeformationPublication deformationPublication,
        BackendReadyFramePackage package,
        in VulkanAdvancedScenePublicationState sceneState,
        int passIndex,
        uint viewMask,
        in RenderGraph.FrameOpContext context,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(resources);
        reason = "Ready";
        if (_recordCount != 0)
            return true;
        if (!sceneState.IsValid ||
            !package.TryGetCanonicalPublicationSnapshot(
                out AdvancedGpuScenePublicationSnapshot publication))
        {
            reason = "the package does not retain the exact canonical geometry publication";
            return false;
        }
        if (deformationSlices.Length != payloads.Length)
        {
            reason = "the canonical visibility payload or deformation column is incomplete";
            return false;
        }
        if (payloads.IsEmpty)
        {
            ThawForReuse();
            _visibilityGeometrySources = new VulkanAdvancedVisibilityGeometrySlices(
                sceneState.StaticVertices,
                new VulkanVisibilityPreparedVertexSource(sceneState.StaticVertices, default, 64u),
                new VulkanVisibilityPreparedVertexSource(sceneState.StaticVertices, default, 64u),
                sceneState.Indices,
                sceneState.MeshletDescriptors,
                sceneState.MeshletVertexIndices,
                sceneState.MeshletTriangleWords,
                default);
            if (!_visibilityGeometrySources.HasValidSources)
            {
                reason = "the empty visibility family has no retained descriptor geometry backing";
                ThawForReuse();
                return false;
            }

            // A zero-draw publication is a complete logical family. Freeze and seal
            // it so later ownership and submission-plan checks retain that identity.
            Freeze();
            _submissionPlansSealed = true;
            return true;
        }

        ReadOnlySpan<AdvancedDrawRecord> canonicalDraws =
            publication.Draws.PhysicalRecords;
        if (canonicalDraws.IsEmpty ||
            !EnsurePayloadCapacity(canonicalDraws.Length))
        {
            reason = "the canonical draw image exceeds the deformation-overlay capacity";
            return false;
        }
        bool requiresDeformation = false;
        for (int index = 0; index < payloads.Length; ++index)
            requiresDeformation |= payloads[index].Skinned;

        // Static-only publications never dispatch deformation. Their unused
        // deformation bindings still need a valid, retained storage range.
        VulkanVisibilityPreparedVertexSource currentVertices = new(
            sceneState.StaticVertices, default, 64u);
        VulkanVisibilityPreparedVertexSource previousVertices = currentVertices;
        if (requiresDeformation && !TryCaptureDeformationSources(
                resources, in deformationPublication,
                out currentVertices, out previousVertices, out reason))
            return false;

        ThawForReuse();
        _deformationOverlayCount = canonicalDraws.Length;
        _deformationOverlay.AsSpan(0, _deformationOverlayCount).Clear();
        _deformationOverlayWrites.AsSpan(0, _deformationOverlayCount).Clear();
        _visibilityGeometrySources = new VulkanAdvancedVisibilityGeometrySlices(
            sceneState.StaticVertices,
            currentVertices,
            previousVertices,
            sceneState.Indices,
            sceneState.MeshletDescriptors,
            sceneState.MeshletVertexIndices,
            sceneState.MeshletTriangleWords,
            default);
        if (!_visibilityGeometrySources.HasValidSources)
        {
            reason = "the visibility geometry sources are incomplete";
            ThawForReuse();
            return false;
        }

        S13aPublicationTelemetry.StepProbe payloadProbe =
            S13aPublicationTelemetry.BeginAdvancedFamilyStep();
        for (int payloadIndex = 0; payloadIndex < payloads.Length; ++payloadIndex)
        {
            AdvancedVisibilityPayload payload = payloads[payloadIndex];
            if (!payload.Draw.IsValid)
                continue;
            if (!publication.Draws.TryGetDenseIndex(
                    payload.Draw,
                    out uint drawDenseIndex) ||
                drawDenseIndex >= (uint)canonicalDraws.Length)
            {
                reason = $"canonical visibility payload {payloadIndex} has no exact draw row";
                ThawForReuse();
                return false;
            }

            AdvancedDrawRecord canonicalDraw =
                canonicalDraws[checked((int)drawDenseIndex)];
            if (canonicalDraw.Geometry != payload.Geometry)
            {
                reason = $"canonical visibility payload {payloadIndex} changed its draw-to-geometry association";
                ThawForReuse();
                return false;
            }

            AdvancedGeometryRecord geometry = default;
            bool geometryResolved =
                payload.Geometry.IsValid &&
                publication.Geometry.TryGet(payload.Geometry, out geometry);
            if (!geometryResolved)
            {
                reason =
                    $"canonical visibility payload {payloadIndex} has no immutable geometry association " +
                    $"(submissionSequence={publication.Submission.Sequence}, " +
                    $"draw={payload.Draw.Index}:{payload.Draw.Generation}, " +
                    $"geometry={payload.Geometry.Index}:{payload.Geometry.Generation}, " +
                    $"geometrySnapshotSequence={publication.Geometry.Sequence}, " +
                    $"geometryRecordCount={publication.Geometry.RecordCount})";
                ThawForReuse();
                return false;
            }

            bool canonicalRangeMatches =
                geometry.Source is EAdvancedGeometrySource.Static or
                    EAdvancedGeometrySource.MeshletLocal &&
                geometry.CurrentVertexData.IsValid &&
                geometry.IndexData.IsValid &&
                geometry.CurrentVertexData.ElementStride == 64u &&
                geometry.IndexData.ElementStride == sizeof(uint) &&
                payload.FirstIndex == geometry.IndexBase &&
                payload.IndexCount == geometry.IndexCount &&
                payload.VertexCount == geometry.VertexCount &&
                (payload.Skinned ||
                 payload.GeometryOffsets.VertexOffset == geometry.VertexBase);
            if (!canonicalRangeMatches)
            {
                reason = $"canonical visibility payload {payloadIndex} does not match its immutable topology " +
                    $"(skinned={payload.Skinned}, source={geometry.Source}, " +
                    $"vertexBase={payload.GeometryOffsets.VertexOffset}/{geometry.VertexBase}, " +
                    $"indexBase={payload.FirstIndex}/{geometry.IndexBase}, " +
                    $"indexCount={payload.IndexCount}/{geometry.IndexCount}, " +
                    $"vertexCount={payload.VertexCount}/{geometry.VertexCount})";
                ThawForReuse();
                return false;
            }

            VulkanVisibilityPreparedVertexSource preparedVertices;
            uint preparedVertexBase;
            if (payload.Skinned)
            {
                AdvancedDeformedArenaSlice slice = deformationSlices[payloadIndex];
                bool offsetsFit =
                    slice.VertexStride == 64u &&
                    slice.VertexCount == payload.VertexCount &&
                    slice.CurrentFrameSlot ==
                        deformationPublication.CurrentFrameSlot &&
                    slice.PreviousFrameSlot ==
                        deformationPublication.PreviousFrameSlot &&
                    slice.CurrentVertexOffset ==
                        payload.GeometryOffsets.VertexOffset &&
                    slice.PreviousVertexOffset ==
                        payload.GeometryOffsets.PreviousVertexOffset &&
                    (ulong)slice.CurrentVertexOffset * slice.VertexStride <=
                        currentVertices.Length &&
                    (ulong)slice.VertexCount * slice.VertexStride <=
                        currentVertices.Length -
                        (ulong)slice.CurrentVertexOffset * slice.VertexStride &&
                    (ulong)slice.PreviousVertexOffset * slice.VertexStride <=
                        previousVertices.Length &&
                    (ulong)slice.VertexCount * slice.VertexStride <=
                        previousVertices.Length -
                        (ulong)slice.PreviousVertexOffset * slice.VertexStride;
                if (deformationPublication.JobCount == 0u ||
                    !slice.Owner.IsValid ||
                    canonicalDraw.Deformation != slice.Owner ||
                    !offsetsFit)
                {
                    ulong currentByteOffset =
                        (ulong)slice.CurrentVertexOffset * slice.VertexStride;
                    ulong previousByteOffset =
                        (ulong)slice.PreviousVertexOffset * slice.VertexStride;
                    ulong sliceByteLength =
                        (ulong)slice.VertexCount * slice.VertexStride;
                    reason =
                        $"canonical visibility payload {payloadIndex} has no exact GPU deformation output " +
                        $"(forceCpuDiagnostic={payload.ForceCpuDiagnostic}, " +
                        $"publishedJobs={deformationPublication.JobCount}, " +
                        $"drawDeformation={canonicalDraw.Deformation.Index}:{canonicalDraw.Deformation.Generation}, " +
                        $"sliceOwner={slice.Owner.Index}:{slice.Owner.Generation}, " +
                        $"stride={slice.VertexStride}/64, " +
                        $"vertexCount={slice.VertexCount}/{payload.VertexCount}, " +
                        $"slots={slice.CurrentFrameSlot}:{slice.PreviousFrameSlot}/" +
                            $"{deformationPublication.CurrentFrameSlot}:{deformationPublication.PreviousFrameSlot}, " +
                        $"offsets={slice.CurrentVertexOffset}:{slice.PreviousVertexOffset}/" +
                            $"{payload.GeometryOffsets.VertexOffset}:{payload.GeometryOffsets.PreviousVertexOffset}, " +
                        $"byteRanges={currentByteOffset}+{sliceByteLength}/{currentVertices.Length}," +
                            $"{previousByteOffset}+{sliceByteLength}/{previousVertices.Length}, " +
                        $"offsetsFit={offsetsFit})";
                    ThawForReuse();
                    return false;
                }

                EAdvancedPreparedDrawDeformationFlags flags =
                    EAdvancedPreparedDrawDeformationFlags.Active |
                    EAdvancedPreparedDrawDeformationFlags.TemporalStatePresent;
                if (deformationPublication.PreviousOutputValid &&
                    slice.HasValidVelocity &&
                    payload.TemporalReason == EAdvancedVelocityValidityReason.Valid)
                {
                    flags |=
                        EAdvancedPreparedDrawDeformationFlags.PreviousValid;
                }
                flags = (EAdvancedPreparedDrawDeformationFlags)
                    AdvancedReconstructionTemporalFlags.PackVelocityReason(
                        (uint)flags,
                        payload.TemporalReason);
                AdvancedPreparedDrawDeformationRecord overlay = new(
                    payload.Geometry,
                    slice.Owner,
                    slice.CurrentVertexOffset,
                    slice.PreviousVertexOffset,
                    slice.VertexCount,
                    flags);
                int overlayIndex = checked((int)drawDenseIndex);
                if (_deformationOverlayWrites[overlayIndex] != 0 &&
                    _deformationOverlay[overlayIndex] != overlay)
                {
                    reason = $"canonical draw row {drawDenseIndex} resolves to conflicting deformation slices";
                    ThawForReuse();
                    return false;
                }
                _deformationOverlay[overlayIndex] = overlay;
                _deformationOverlayWrites[overlayIndex] = 1;
                preparedVertices = currentVertices;
                preparedVertexBase = slice.CurrentVertexOffset;
            }
            else
            {
                EAdvancedPreparedDrawDeformationFlags flags =
                    (EAdvancedPreparedDrawDeformationFlags)
                    AdvancedReconstructionTemporalFlags.PackVelocityReason(
                        (uint)EAdvancedPreparedDrawDeformationFlags.TemporalStatePresent,
                        payload.TemporalReason);
                AdvancedPreparedDrawDeformationRecord overlay = new(
                    payload.Geometry,
                    canonicalDraw.Deformation,
                    geometry.VertexBase,
                    geometry.VertexBase,
                    payload.VertexCount,
                    flags);
                int overlayIndex = checked((int)drawDenseIndex);
                if (_deformationOverlayWrites[overlayIndex] != 0 &&
                    _deformationOverlay[overlayIndex] != overlay)
                {
                    reason = $"canonical draw row {drawDenseIndex} resolves to conflicting temporal states";
                    ThawForReuse();
                    return false;
                }
                _deformationOverlay[overlayIndex] = overlay;
                _deformationOverlayWrites[overlayIndex] = 1;
                preparedVertices = new VulkanVisibilityPreparedVertexSource(
                    sceneState.StaticVertices,
                    default,
                    64u);
                preparedVertexBase = geometry.VertexBase;
            }

            VulkanVisibilityGeometryRecordClosure geometryClosure = new(
                payload.Geometry,
                geometry,
                sceneState.StaticVertices,
                sceneState.Indices,
                preparedVertices,
                preparedVertexBase,
                sceneState.NativeGeneration);
            if (!geometryClosure.TryValidate(
                    resources,
                    in sceneState,
                    out reason))
            {
                reason = $"canonical visibility payload {payloadIndex}: {reason}";
                ThawForReuse();
                return false;
            }

            VkBufferHandle vertices = preparedVertices.Buffer;
            VkBufferHandle indices = sceneState.Indices.Buffer;
            ulong vertexSignature = MixVisibilityKey(
                vertices.Handle,
                preparedVertices.Generation,
                preparedVertices.Offset,
                preparedVertices.Length,
                preparedVertices.ElementStride);
            PendingMeshDraw draw = default(PendingMeshDraw) with
            {
                Renderer = null!,
                RasterizationSamples = SampleCountFlags.Count1Bit,
                DepthTestEnabled = true,
                DepthWriteEnabled = true,
                DepthCompareOp = CompareOp.LessOrEqual,
                CullMode = payload.CullMode == 0u
                    ? CullModeFlags.None
                    : CullModeFlags.BackBit,
                FrontFace = FrontFace.CounterClockwise,
                ColorWriteMask = ColorComponentFlags.RBit |
                    ColorComponentFlags.GBit |
                    ColorComponentFlags.BBit |
                    ColorComponentFlags.ABit,
                Instances = Math.Max(1u, payload.InstanceCount),
            };
            VulkanPreparedMeshPrimitive primitive = new(
                default,
                PrimitiveTopology.TriangleList,
                indices,
                IndexType.Uint32,
                payload.IndexCount,
                Indexed: true);
            VulkanResidentDrawTemplateNativeState native = new(
                default,
                in primitive,
                vertices,
                0u,
                vertexSignature,
                in draw);
            VulkanRenderBinKey key = VulkanRenderBinKey.CreateVisibilityGeometry(
                passIndex,
                viewMask,
                in payload,
                sceneState.NativeGeneration,
                in preparedVertices,
                sceneState.Indices,
                in native,
                in context);
            // Skipped payloads do not consume a row. Grow the compact row
            // before its manifest is accessed, not after it is built.
            int recordIndex = _recordCount;
            if (!EnsureRowCapacity(recordIndex + 1))
            {
                reason = "the canonical visibility stream exceeded its fixed capacity";
                ThawForReuse();
                return false;
            }
            VulkanTemplateResourceManifest manifest =
                VisibilityAtlasManifest(recordIndex);
            manifest.ResetVisibilityGeometry(
                in payload,
                in preparedVertices,
                sceneState.Indices);
            if (!TryAppend(
                    new VulkanPreparedStableBinRecord(
                        key,
                        default,
                        payloadIndex,
                        0,
                        0,
                        manifest,
                        payloadIndex,
                        new VulkanPreparedVisibilityDirectDraw(
                            payload.IndexCount,
                            Math.Max(1u, payload.InstanceCount),
                            payload.FirstIndex,
                            checked((int)preparedVertexBase),
                            checked((uint)payloadIndex)),
                        payload.Material.Index,
                        payload.Draw.Index,
                        native,
                        geometryClosure,
                        canonicalDraw.Flags),
                    ReadOnlySpan<FrameOpResourceUse>.Empty))
            {
                reason = "the canonical visibility stream exceeded its fixed capacity";
                ThawForReuse();
                return false;
            }
        }
        payloadProbe.End(S13aAdvancedFamilyStep.BinGeometryPayloads);

        S13aPublicationTelemetry.StepProbe freezeProbe =
            S13aPublicationTelemetry.BeginAdvancedFamilyStep();
        Freeze();
        freezeProbe.End(S13aAdvancedFamilyStep.BinGeometryFreeze);
        return true;
    }

    private static bool TryCaptureDeformationSources(
        VulkanResourceRuntime resources,
        in AdvancedGpuDeformationPublication publication,
        out VulkanVisibilityPreparedVertexSource current,
        out VulkanVisibilityPreparedVertexSource previous,
        out string reason)
    {
        current = default;
        previous = default;
        reason = "the aggregate deformation output publication is incomplete";
        if (publication.ResourceGeneration == 0u || publication.JobCount == 0u ||
            publication.CurrentVertices is not { Length: > 0u } currentOwner ||
            !resources.TryCaptureNativeBufferRange(
                currentOwner, 0u, currentOwner.Length,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit,
                out VulkanNativeBufferRange currentRange, out reason))
            return false;

        current = new(default, currentRange, 64u);
        // The first deformation publication has no previous GPU output. Its
        // overlay disables history reads; alias the current retained range so
        // the descriptor remains valid without inventing a prior generation.
        previous = current;
        if (!publication.PreviousOutputValid)
            return true;
        if (publication.PreviousVertices is not { Length: > 0u } previousOwner ||
            !resources.TryCaptureNativeBufferRange(
                previousOwner, 0u, previousOwner.Length,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit,
                out VulkanNativeBufferRange previousRange, out reason))
            return false;

        previous = new(default, previousRange, 64u);
        return true;
    }

    private static ulong MixVisibilityKey(params ReadOnlySpan<ulong> values)
    {
        ulong hash = 14695981039346656037UL;
        for (int index = 0; index < values.Length; ++index)
            hash = (hash ^ values[index]) * 1099511628211UL;
        return hash == 0u ? 1u : hash;
    }

    internal void Clear()
    {
        if (_frozen)
            throw new InvalidOperationException("A frozen stable-bin stream cannot be mutated.");
        _recordCount = 0;
        _lateResourceUseCount = 0;
        _headerCount = 0;
        _submissionPlansSealed = false;
        _deformationOverlayCount = 0;
        _visibilityGeometrySources = default;
        _cpuIndirectParity.Reset();
        _exceptions.Clear();
    }

    internal bool TryAppend(
        in VulkanPreparedStableBinRecord record,
        ReadOnlySpan<FrameOpResourceUse> lateResourceUses)
    {
        if (_frozen ||
            !EnsureRowCapacity(_recordCount + 1) ||
            !EnsureLateResourceUseCapacity(_lateResourceUseCount + lateResourceUses.Length))
        {
            return false;
        }

        // Immutable descriptor/buffer reads are retained by the resident
        // template/bin manifest. Carry only target and frame-scope uses into
        // the current frame; retaining the imported draw resources here would
        // duplicate the template lifetime authority once per visible draw.
        int resourceOffset = _lateResourceUseCount;
        int retainedUseCount = 0;
        for (int index = 0; index < lateResourceUses.Length; ++index)
        {
            FrameOpResourceUse use = lateResourceUses[index];
            if ((use.Access & EFrameOpResourceAccess.Imported) != 0)
                continue;
            _lateResourceUses[resourceOffset + retainedUseCount++] = use;
        }
        _lateResourceUseCount += retainedUseCount;
        _records[_recordCount++] = record with
        {
            LateResourceUseOffset = resourceOffset,
            LateResourceUseCount = retainedUseCount,
        };
        return true;
    }

    internal bool TryAppendException(
        in AdvancedGpuSceneDrawIdentitySnapshot draw,
        VulkanBinOrderedExceptionReason reason,
        ulong sequence)
        => !_frozen && _exceptions.TryAppend(in draw, reason, sequence);

    internal bool TryAppendException(in VulkanBinOrderedException exception)
    {
        VulkanBinOrderedException copy = exception;
        AdvancedGpuSceneDrawIdentitySnapshot draw = copy.Draw;
        return !_frozen && _exceptions.TryAppend(
            in draw, copy.Reason, copy.Sequence);
    }

    /// <summary>Freezes a deterministic key/handle order for recording workers.</summary>
    internal void Freeze()
    {
        if (_frozen)
            return;
        SortRecordsForFreeze();
        _headerCount = 0;
        _submissionPlansSealed = false;
        _frozen = true;
        if (VulkanFeatureProfile.ActiveProfile is
            EVulkanGpuDrivenProfile.DevParity or EVulkanGpuDrivenProfile.Diagnostics)
            _ = TryBuildCpuIndirectParity(out _);
    }

    /// <summary>
    /// Orders the appended records by bin key, template and ingress identity.
    /// The compact keys are sorted separately and the permutation is applied
    /// in place by following each cycle once, so every record moves at most
    /// once instead of shifting through an insertion sort; the appended
    /// position is the final tiebreaker, which reproduces a stable sort's
    /// order exactly. The result is verified against the full comparison when
    /// observation is enabled.
    /// </summary>
    private void SortRecordsForFreeze()
    {
        int count = _recordCount;
        if (count < 2)
            return;
        Span<VulkanPreparedStableBinSortKey> keys = _freezeSortKeys.AsSpan(0, count);
        Span<int> order = _freezeSortOrder.AsSpan(0, count);
        for (int index = 0; index < count; ++index)
        {
            keys[index] = VulkanPreparedStableBinSortKey.From(in _records[index], index);
            order[index] = index;
        }
        keys.Sort(order);
        // order[position] is the source index of the record that belongs at
        // that position; each cycle is rotated once through one displaced copy.
        for (int position = 0; position < count; ++position)
        {
            if (order[position] == position)
                continue;
            VulkanPreparedStableBinRecord displaced = _records[position];
            int destination = position;
            while (true)
            {
                int source = order[destination];
                order[destination] = destination;
                if (source == position)
                {
                    _records[destination] = displaced;
                    break;
                }
                _records[destination] = _records[source];
                destination = source;
            }
        }
        if (S13aPublicationTelemetry.Enabled)
            S13aPublicationTelemetry.AdvancedBinFreezeOrder(count, CountFreezeOrderViolations());
    }

    private int CountFreezeOrderViolations()
    {
        int violations = 0;
        for (int index = 1; index < _recordCount; ++index)
            if (Compare(_records[index], _records[index - 1]) < 0)
                violations++;
        return violations;
    }

    internal void ThawForReuse()
    {
        ReleaseRetainedTemplates();
        _frozen = false;
        _recordCount = 0;
        _lateResourceUseCount = 0;
        _headerCount = 0;
        _submissionPlansSealed = false;
        _deformationOverlayCount = 0;
        _visibilityGeometrySources = default;
        _cpuIndirectParity.Reset();
        _exceptions.Clear();
    }

    /// <summary>
    /// Pins the exact resident templates referenced by this accepted frame
    /// plan. The frame plan is reset only after rejection or frame-slot GPU
    /// completion, so these leases cover primary recording and submission
    /// without depending on an unrelated ordinary-mesh prepared recording.
    /// </summary>
    internal bool TryRetainTemplatesForFramePlan(
        VulkanResidentDrawTemplateTable residentTemplates,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(residentTemplates);
        if (!_submissionPlansSealed)
        {
            reason = "stable-bin submission plans were not sealed";
            return false;
        }
        if (_retainedTemplateCount == _recordCount)
        {
            reason = "Ready";
            return true;
        }
        if (_retainedTemplateCount != 0)
            throw new InvalidOperationException(
                "A stable-bin frame plan has a partial resident-template lease set.");

        for (int index = 0; index < _recordCount; ++index)
        {
            VulkanPreparedStableBinRecord record = _records[index];
            VulkanResidentDrawTemplateHandle handle = record.Template;
            if (!handle.IsValid)
            {
                VulkanResidentDrawTemplateNativeState native =
                    record.VisibilityNativeState;
                if (native.PrimitiveCount != 1 ||
                    native.Primitive0.IndexBuffer.Handle == 0 ||
                    native.VertexBufferCount == 0)
                {
                    ReleaseRetainedTemplates();
                    reason = "a canonical visibility record has no frozen atlas geometry";
                    return false;
                }
                _retainedTemplates[_retainedTemplateCount++] = null;
                continue;
            }
            if (!residentTemplates.TryGetResolvedAndRetain(
                    handle,
                    out VulkanResidentDrawTemplate? template) ||
                template is null)
            {
                ReleaseRetainedTemplates();
                reason = $"resident template {handle} is no longer live";
                return false;
            }
            _retainedTemplates[_retainedTemplateCount++] = template;
        }

        reason = "Ready";
        return true;
    }

    private void ReleaseRetainedTemplates()
    {
        for (int index = 0; index < _retainedTemplateCount; ++index)
        {
            _retainedTemplates[index]?.ReleaseUse();
            _retainedTemplates[index] = null;
        }
        _retainedTemplateCount = 0;
    }

    internal void CopyFrom(VulkanPreparedStableBinStream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source._recordCount > _maximumRowCapacity ||
            source._headerCount > _maximumRowCapacity ||
            source._lateResourceUseCount > _maximumResourceUseCapacity ||
            source._deformationOverlayCount > _maximumRowCapacity ||
            source._exceptions.Count > _exceptions.Capacity)
        {
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.MainScene,
                Math.Min(
                    Math.Min(_maximumRowCapacity, _maximumResourceUseCapacity),
                    _exceptions.Capacity),
                Math.Max(
                    Math.Max(source._recordCount, source._lateResourceUseCount),
                    source._exceptions.Count));
        }

        ThawForReuse();
        // The checks above bound every count by the declared capacities, so
        // growth of the thawed stream cannot fail.
        EnsureRowCapacity(Math.Max(source._recordCount, source._headerCount));
        EnsureLateResourceUseCapacity(source._lateResourceUseCount);
        EnsurePayloadCapacity(source._deformationOverlayCount);
        source.Records.CopyTo(_records);
        for (int recordIndex = 0; recordIndex < source._recordCount; ++recordIndex)
        {
            VulkanPreparedStableBinRecord record = _records[recordIndex];
            if (record.Template.IsValid)
                continue;
            VulkanTemplateResourceManifest manifest =
                VisibilityAtlasManifest(recordIndex);
            manifest.CopyFrom(record.TemplateManifest);
            _records[recordIndex] = record with { TemplateManifest = manifest };
        }
        source.LateResourceUses.CopyTo(_lateResourceUses);
        source.DeformationOverlay.CopyTo(_deformationOverlay);
        source._deformationOverlayWrites.AsSpan(
            0, source._deformationOverlayCount).CopyTo(
                _deformationOverlayWrites);
        _deformationOverlayCount = source._deformationOverlayCount;
        _visibilityGeometrySources = source._visibilityGeometrySources;
        foreach (VulkanBinOrderedException exception in source.OrderedExceptions)
            _exceptions.TryAppend(exception.Draw, exception.Reason, exception.Sequence);
        _recordCount = source._recordCount;
        _lateResourceUseCount = source._lateResourceUseCount;
        _headerCount = source._headerCount;
        source.Headers.CopyTo(_headers);
        if (!_sealedExceptions.TryReset(source._sealedExceptions.Entries))
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.MainScene,
                _exceptions.Capacity,
                source._sealedExceptions.Entries.Length);
        for (int headerIndex = 0; headerIndex < _headerCount; ++headerIndex)
        {
            VulkanPreparedStableBinHeader header = _headers[headerIndex];
            VulkanBinResourceManifest manifest = header.ResourceManifest;
            if (manifest.IsStreamOwned)
            {
                int recordCount = header.RecordCount;
                if (recordCount < 0 || header.RecordOffset < 0 ||
                    header.RecordOffset > _records.Length - recordCount ||
                    !VisibilityManifestView(headerIndex).TryCopyFrom(
                        manifest,
                        checked(header.RecordOffset * 3),
                        checked(recordCount * 3),
                        checked(header.RecordOffset * 2),
                        checked(recordCount * 2),
                        out _))
                {
                    throw new VulkanAcceptedFramePlanCapacityException(
                        EVulkanAcceptedFrameLane.MainScene,
                        _records.Length,
                        source._recordCount);
                }
                manifest = VisibilityManifestView(headerIndex);
                header = header with { ResourceManifest = manifest };
            }
            if (header.SubmissionPlan is not { } sourcePlan)
            {
                _sealScratchPlanAssigned[headerIndex] = 0;
                _headers[headerIndex] = header;
                continue;
            }
            VulkanSealedBinSubmissionPlan destinationPlan =
                RentSealScratchPlan(headerIndex);
            destinationPlan.CopyFrom(sourcePlan, _sealedExceptions, manifest);
            _sealScratchPlanAssigned[headerIndex] = 1;
            _headers[headerIndex] = header with
            {
                SubmissionPlan = destinationPlan,
            };
        }
        _submissionPlansSealed = source._submissionPlansSealed;
        if (source._cpuIndirectParity.IsSealed &&
            !_cpuIndirectParity.TryBuild(Records))
        {
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.MainScene,
                _cpuIndirectParity.Capacity,
                source._cpuIndirectParity.Count);
        }
        if (source._frozen)
            _frozen = true;
    }

    /// <summary>
    /// Resolves cold topology manifests after the per-frame stream is frozen.
    /// The cache is invalidated on membership topology change; output/capability
    /// plan resolution remains an explicit later seal step.
    /// </summary>
    internal bool TryResolveManifests(
        VulkanStableBinManifestCache cache,
        ulong topologyGeneration)
        => TryResolveManifests(cache, topologyGeneration, out _);

    internal bool TryResolveManifests(
        VulkanStableBinManifestCache cache,
        ulong topologyGeneration,
        out VulkanBinResourceManifestFailure failure)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (!_frozen)
            throw new InvalidOperationException("Stable-bin manifests require a frozen stream.");

        failure = VulkanBinResourceManifestFailure.None;
        _headerCount = 0;
        _submissionPlansSealed = false;
        for (int start = 0; start < _recordCount;)
        {
            VulkanRenderBinKey key = _records[start].Key;
            int end = start + 1;
            while (end < _recordCount && _records[end].Key == key)
                ++end;

            int templateCount = end - start;
            bool canonicalVisibility = true;
            for (int index = 0; index < templateCount; ++index)
            {
                VulkanPreparedStableBinRecord record = _records[start + index];
                _manifestTemplates[index] = record.TemplateManifest;
                canonicalVisibility &= !record.Template.IsValid;
            }

            VulkanBinResourceManifest? manifest;
            if (canonicalVisibility)
            {
                if (_headerCount == _visibilityManifestViews.Length ||
                    !VisibilityManifestView(_headerCount).TryResetFromTemplates(
                        _manifestTemplates.AsSpan(0, templateCount),
                        checked(start * 3),
                        checked(templateCount * 3),
                        checked(start * 2),
                        checked(templateCount * 2),
                        out failure))
                {
                    if (failure == VulkanBinResourceManifestFailure.None)
                        failure = VulkanBinResourceManifestFailure.CapacityExceeded;
                    return false;
                }
                manifest = VisibilityManifestView(_headerCount);
            }
            else if (!cache.TryGet(topologyGeneration, key, out manifest))
            {
                int resourceCapacity = 0;
                int nativeUseCapacity = 0;
                for (int index = 0; index < templateCount; ++index)
                {
                    VulkanTemplateResourceManifest template = _manifestTemplates[index];
                    resourceCapacity = checked(resourceCapacity + template.Count);
                    nativeUseCapacity = checked(nativeUseCapacity + template.NativeUseCount);
                }
                if (!VulkanBinResourceManifest.TryCreate(
                        _manifestTemplates.AsSpan(0, templateCount),
                        resourceCapacity,
                        nativeUseCapacity,
                        out manifest,
                        out failure))
                {
                    return false;
                }
                cache.Store(topologyGeneration, key, manifest!);
            }
            if (_headerCount == _headers.Length)
            {
                failure = VulkanBinResourceManifestFailure.CapacityExceeded;
                return false;
            }
            _headers[_headerCount++] = new(key, start, end - start, manifest!);
            start = end;
        }
        return true;
    }

    /// <summary>
    /// Seals each frozen opaque bin against the already-published visibility
    /// producer layout. A visibility indirect range has one GPU counter and
    /// one contiguous argument slice, so it may back exactly one stable bin;
    /// a shared range would make per-bin recording ambiguous and is rejected.
    /// </summary>
    internal bool TrySealSubmissionPlans(
        ReadOnlySpan<int> payloadIndexByIngressIndex,
        ReadOnlySpan<AdvancedIndirectRange> indirectRanges,
        ReadOnlySpan<int> indirectPayloadIndices,
        EMeshSubmissionStrategy requestedStrategy,
        in VulkanSubmissionLaneCapabilities capabilities,
        in VulkanSubmissionOutputPolicy outputPolicy,
        GpuDiagnosticReadbackPlanNode? diagnosticPlan,
        bool requestCpuSafetyNet,
        out VulkanSubmissionPlanRejectionReason rejection)
    {
        if (!_frozen)
            throw new InvalidOperationException("Stable-bin submission plans require a frozen stream.");

        rejection = VulkanSubmissionPlanRejectionReason.None;
        _submissionPlansSealed = false;
        if (!outputPolicy.AllowsCanonicalVisibilityFamily)
        {
            rejection = VulkanSubmissionPlanRejectionReason.CanonicalVisibilityOutputPolicyRejected;
            return false;
        }
        Array.Clear(_sealScratchPlanAssigned, 0, _headerCount);
        Array.Clear(_sealScratchRanges, 0, _headerCount);
        if (_headerCount == 0)
        {
            _submissionPlansSealed = true;
            return true;
        }
        if (payloadIndexByIngressIndex.IsEmpty || indirectRanges.IsEmpty ||
            indirectPayloadIndices.IsEmpty)
        {
            rejection = VulkanSubmissionPlanRejectionReason.IndirectRangeUnresolved;
            return false;
        }
        if (!_sealedExceptions.TryReset(OrderedExceptions))
        {
            rejection = VulkanSubmissionPlanRejectionReason.OrderedExceptionCapacityExceeded;
            return false;
        }
        if (!TryBuildRangeIndexByPayload(
                indirectRanges,
                indirectPayloadIndices,
                out rejection))
        {
            return false;
        }
        for (int headerIndex = 0; headerIndex < _headerCount; ++headerIndex)
        {
            VulkanPreparedStableBinHeader header = _headers[headerIndex];
            if (!TryResolveExactRange(
                    in header,
                    payloadIndexByIngressIndex,
                    indirectRanges,
                    indirectPayloadIndices,
                    out int rangeIndex,
                    out AdvancedIndirectRange range,
                    out rejection))
            {
                return false;
            }
            if (!TryResolveRangeExecutionStrategy(
                    requestedStrategy,
                    range.Key.Producer,
                    out EMeshSubmissionStrategy rangeStrategy))
            {
                rejection = VulkanSubmissionPlanRejectionReason.RangeExecutionLaneMismatch;
                return false;
            }
            // Visibility ranges are keyed by exact geometry and raster state.
            // Ordinary material-pipeline bins may subdivide that range even
            // though the visibility program does not. GPU lanes therefore
            // record the range once from its first compatible header; later
            // ordinary-bin headers remain retained but emit no duplicate draw.
            int priorRangeOwner = -1;
            for (int prior = 0; prior < headerIndex; ++prior)
            {
                if (_sealScratchPlanAssigned[prior] != 0 &&
                    _sealScratchRanges[prior].Key == range.Key &&
                    _sealScratchRanges[prior].FirstPayloadIndex == range.FirstPayloadIndex)
                {
                    priorRangeOwner = prior;
                    break;
                }
            }
            if (priorRangeOwner >= 0 && rangeStrategy != EMeshSubmissionStrategy.CpuDirect)
            {
                _sealScratchRanges[headerIndex] = range;
                continue;
            }

            GpuDiagnosticReadbackPlanNode? rangeDiagnosticPlan =
                ResolveRangeDiagnosticPlan(diagnosticPlan, rangeStrategy, in range);

            if (!VulkanBinSubmissionPlanResolver.TrySeal(
                    header.Key,
                    header.ResourceManifest,
                    requestedStrategy,
                    rangeStrategy,
                    in capabilities,
                    in outputPolicy,
                    sourceCount: rangeStrategy == EMeshSubmissionStrategy.CpuDirect
                        ? checked((uint)header.RecordCount)
                        : range.PayloadCapacity,
                    sourceCapacity: rangeStrategy == EMeshSubmissionStrategy.CpuDirect
                        ? checked((uint)header.RecordCount)
                        : range.PayloadCapacity,
                    maxOutputPerSource: 1u,
                    outputCapacity: range.PayloadCapacity,
                    rangeDiagnosticPlan,
                    requestCpuSafetyNet,
                    _sealedExceptions,
                    RentSealScratchPlan(headerIndex),
                    out VulkanSealedBinSubmissionPlan? plan,
                    out rejection))
            {
                return false;
            }

            if (!ReferenceEquals(plan, _sealScratchPlans[headerIndex]))
                throw new InvalidOperationException(
                    "The sealed-bin resolver replaced its preallocated plan slot.");
            _sealScratchPlanAssigned[headerIndex] = 1;
            _sealScratchRanges[headerIndex] = range;
        }

        // One global counter copy after the final instrumented range reports
        // sticky producer overflow asynchronously. It must never attach to a
        // zero-readback lane, and per-range copies would only duplicate work.
        if (diagnosticPlan.HasValue)
        {
            for (int headerIndex = _headerCount - 1; headerIndex >= 0; --headerIndex)
            {
                if (_sealScratchPlanAssigned[headerIndex] == 0)
                    continue;

                VulkanSealedBinSubmissionPlan plan = _sealScratchPlans[headerIndex]!;
                if (!plan.IsInstrumented || plan.DiagnosticPlan is not { } attachedDiagnostic)
                    continue;

                plan.AttachOverflowDiagnosticPlan(diagnosticPlan.Value with
                {
                    SourceByteOffset = 0u,
                    ByteCount = 64u,
                    Strategy = attachedDiagnostic.Strategy,
                    Decoder = EGpuDiagnosticReadbackDecoder.SubmissionValidation,
                });
                break;
            }
        }

        for (int headerIndex = 0; headerIndex < _headerCount; ++headerIndex)
        {
            VulkanPreparedStableBinHeader header = _headers[headerIndex];
            _headers[headerIndex] = header with
            {
                SubmissionPlan = _sealScratchPlanAssigned[headerIndex] != 0
                    ? _sealScratchPlans[headerIndex]
                    : null,
                IndirectRange = _sealScratchRanges[headerIndex],
            };
        }
        _submissionPlansSealed = true;
        return true;
    }

    private static GpuDiagnosticReadbackPlanNode? ResolveRangeDiagnosticPlan(
        GpuDiagnosticReadbackPlanNode? familyPlan,
        EMeshSubmissionStrategy executionStrategy,
        in AdvancedIndirectRange range)
    {
        if (!familyPlan.HasValue)
            return null;

        GpuDiagnosticReadbackPlanNode plan = familyPlan.Value;
        try
        {
            bool indexed = executionStrategy ==
                EMeshSubmissionStrategy.GpuIndirectInstrumented;
            return plan with
            {
                SourceByteOffset = indexed
                    ? range.CountBufferOffset
                    : checked(range.FirstPayloadIndex * 12u),
                ByteCount = indexed
                    ? sizeof(uint)
                    : checked(range.PayloadCapacity * 12u),
                Strategy = executionStrategy,
                Decoder = indexed
                    ? EGpuDiagnosticReadbackDecoder.IndirectDrawCount
                    : EGpuDiagnosticReadbackDecoder.MeshletVisibility,
            };
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool TryResolveRangeExecutionStrategy(
        EMeshSubmissionStrategy requestedStrategy,
        EAdvancedGeometryProducer producer,
        out EMeshSubmissionStrategy strategy)
    {
        if (requestedStrategy == EMeshSubmissionStrategy.CpuDirect)
        {
            strategy = EMeshSubmissionStrategy.CpuDirect;
            return producer is EAdvancedGeometryProducer.CpuDirectStaticIndexed or
                EAdvancedGeometryProducer.CpuDirectPreSkinned;
        }

        if (requestedStrategy is EMeshSubmissionStrategy.GpuIndirectZeroReadback or
            EMeshSubmissionStrategy.GpuIndirectInstrumented)
        {
            strategy = requestedStrategy;
            return producer == EAdvancedGeometryProducer.IndirectIndexed;
        }

        if (requestedStrategy is EMeshSubmissionStrategy.GpuMeshletZeroReadback or
            EMeshSubmissionStrategy.GpuMeshletInstrumented)
        {
            if (producer is EAdvancedGeometryProducer.StaticMeshlet or
                EAdvancedGeometryProducer.SkinnedMeshlet)
            {
                strategy = requestedStrategy;
                return true;
            }
            if (producer == EAdvancedGeometryProducer.IndirectIndexed)
            {
                strategy = requestedStrategy ==
                    EMeshSubmissionStrategy.GpuMeshletInstrumented
                        ? EMeshSubmissionStrategy.GpuIndirectInstrumented
                        : EMeshSubmissionStrategy.GpuIndirectZeroReadback;
                return true;
            }
        }

        strategy = default;
        return false;
    }

    /// <summary>
    /// Builds the ingress-to-canonical-payload join from retained template
    /// identities, then seals without consulting authoring draw objects. This
    /// coordinator-only overload keeps the join allocation-free and prevents
    /// recording workers from resolving resident handles.
    /// </summary>
    internal bool TrySealSubmissionPlans(
        VulkanResidentDrawTemplateTable residentTemplates,
        ReadOnlySpan<AdvancedVisibilityPayload> payloads,
        ReadOnlySpan<AdvancedIndirectRange> indirectRanges,
        ReadOnlySpan<int> indirectPayloadIndices,
        EMeshSubmissionStrategy requestedStrategy,
        in VulkanSubmissionLaneCapabilities capabilities,
        in VulkanSubmissionOutputPolicy outputPolicy,
        GpuDiagnosticReadbackPlanNode? diagnosticPlan,
        bool requestCpuSafetyNet,
        out VulkanSubmissionPlanRejectionReason rejection)
    {
        ArgumentNullException.ThrowIfNull(residentTemplates);
        _payloadIndexByIngressScratch.AsSpan().Fill(-1);
        for (int recordIndex = 0; recordIndex < _recordCount; ++recordIndex)
        {
            VulkanPreparedStableBinRecord record = _records[recordIndex];
            if (record.IngressIndex < 0 ||
                !EnsurePayloadCapacity(record.IngressIndex + 1))
            {
                rejection = VulkanSubmissionPlanRejectionReason.IndirectRangeUnresolved;
                return false;
            }

            if (!record.Template.IsValid)
            {
                int canonicalPayloadIndex = record.VisibilityPayloadIndex;
                if ((uint)canonicalPayloadIndex >= (uint)payloads.Length)
                {
                    rejection = VulkanSubmissionPlanRejectionReason.IndirectRangeUnresolved;
                    return false;
                }
                _payloadIndexByIngressScratch[record.IngressIndex] =
                    canonicalPayloadIndex;
                continue;
            }
            if (!residentTemplates.TryGetLive(
                    record.Template,
                    out VulkanResidentDrawTemplate? template) ||
                template is null)
            {
                rejection = VulkanSubmissionPlanRejectionReason.IndirectRangeUnresolved;
                return false;
            }

            AdvancedGpuHandle draw = template.StructuralIdentity.CanonicalDraw.Primary.Handle;
            int payloadIndex = -1;
            for (int index = 0; index < payloads.Length; ++index)
            {
                if (payloads[index].Draw != draw)
                    continue;
                payloadIndex = index;
                break;
            }
            if (payloadIndex < 0)
            {
                rejection = VulkanSubmissionPlanRejectionReason.IndirectRangeUnresolved;
                return false;
            }
            _payloadIndexByIngressScratch[record.IngressIndex] = payloadIndex;
            AdvancedVisibilityPayload payload = payloads[payloadIndex];
            VulkanResidentDrawTemplateNativeState native = template.NativeState;
            if (native.PrimitiveCount != 1 || !native.Primitive0.Indexed ||
                native.Primitive0.IndexBuffer.Handle == 0 ||
                native.Primitive0.ElementCount == 0u)
            {
                rejection = VulkanSubmissionPlanRejectionReason.IndirectRangeUnresolved;
                return false;
            }
            _records[recordIndex] = record with
            {
                VisibilityPayloadIndex = payloadIndex,
                VisibilityDirectDraw = new VulkanPreparedVisibilityDirectDraw(
                    native.Primitive0.ElementCount,
                    Math.Max(payload.InstanceCount, 1u),
                    0u,
                    0,
                    checked((uint)payloadIndex)),
                VisibilityMaterialIndex = payload.Material.Index,
                VisibilityObjectIndex = payload.Draw.Index,
            };
        }

        return TrySealSubmissionPlans(
            _payloadIndexByIngressScratch,
            indirectRanges,
            indirectPayloadIndices,
            requestedStrategy,
            in capabilities,
            in outputPolicy,
            diagnosticPlan,
            requestCpuSafetyNet,
            out rejection);
    }

    /// <summary>
    /// Seals an opt-in CPU-built indexed-indirect parity artifact from the
    /// exact frozen records. This is diagnostics-only: it does not publish a
    /// Vulkan buffer, change the selected strategy, or allow a CPU fallback.
    /// </summary>
    internal bool TryBuildCpuIndirectParity(
        out VulkanCpuIndirectParityArtifact artifact)
    {
        if (!_frozen || !_submissionPlansSealed)
        {
            artifact = _cpuIndirectParity;
            artifact.Reject(
                VulkanCpuIndirectParityFailure.FrozenStreamUnavailable);
            return false;
        }

        artifact = _cpuIndirectParity;
        return artifact.TryBuild(Records);
    }

    /// <summary>
    /// Acquires all resident template dependencies before command recording and
    /// transfers them to the prepared-frame owner. The owner's normal
    /// frame-slot transfer retires these exact uses after GPU completion.
    /// </summary>
    internal bool TryRetainTemplatesForRecording(
        VulkanResidentDrawTemplateTable residentTemplates,
        VulkanPreparedFrameRecording preparedFrame,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(residentTemplates);
        ArgumentNullException.ThrowIfNull(preparedFrame);
        if (!_submissionPlansSealed)
        {
            reason = "stable-bin submission plans were not sealed";
            return false;
        }

        for (int index = 0; index < _recordCount; ++index)
        {
            VulkanResidentDrawTemplateHandle handle = _records[index].Template;
            if (!residentTemplates.TryGetResolvedAndRetain(handle, out VulkanResidentDrawTemplate? template) ||
                template is null)
            {
                reason = $"resident template {handle} is no longer live";
                return false;
            }
            if (!preparedFrame.TryAdoptResidentTemplateUse(template, out reason))
                return false;
        }

        reason = "Ready";
        return true;
    }

    /// <summary>
    /// Freezes the indexed arguments consumed by the visibility producer in
    /// the same coordinate system as the retained native vertex/index buffers.
    /// Canonical scene payloads retain their handles and material identity, but
    /// atlas-relative draw offsets must never be paired with renderer-local
    /// Vulkan buffers during raster recording.
    /// </summary>
    internal bool TryBuildVisibilityRasterPayloads(
        ReadOnlySpan<AdvancedVisibilityPayload> sourcePayloads,
        out ReadOnlySpan<AdvancedVisibilityPayload> rasterPayloads,
        out string reason)
    {
        rasterPayloads = default;
        if (_submissionPlansSealed && _retainedTemplateCount == 0 &&
            _recordCount == 0 && sourcePayloads.IsEmpty)
        {
            rasterPayloads = ReadOnlySpan<AdvancedVisibilityPayload>.Empty;
            reason = "Ready";
            return true;
        }
        if (!_submissionPlansSealed ||
            _retainedTemplateCount != _recordCount ||
            sourcePayloads.IsEmpty ||
            !EnsurePayloadCapacity(sourcePayloads.Length))
        {
            reason = "stable submission plans, resident-template leases, or source payload capacity are unavailable";
            return false;
        }

        sourcePayloads.CopyTo(_visibilityRasterPayloads);
        _visibilityRasterPayloadWrites.AsSpan(0, sourcePayloads.Length).Clear();
        for (int recordIndex = 0; recordIndex < _recordCount; ++recordIndex)
        {
            VulkanPreparedStableBinRecord record = _records[recordIndex];
            int payloadIndex = record.VisibilityPayloadIndex;
            if ((uint)payloadIndex >= (uint)sourcePayloads.Length)
            {
                reason = "a retained stable-bin record has no exact visibility payload";
                return false;
            }

            VulkanResidentDrawTemplateNativeState native =
                ResolveVisibilityNativeState(recordIndex);
            VulkanPreparedMeshPrimitive primitive = native.Primitive0;
            if (native.PrimitiveCount != 1 ||
                !primitive.Indexed || primitive.IndexBuffer.Handle == 0 ||
                primitive.ElementCount == 0u)
            {
                reason = "visibility raster requires one non-empty indexed native primitive per payload";
                return false;
            }

            AdvancedVisibilityPayload source = sourcePayloads[payloadIndex];
            if (!record.Template.IsValid)
            {
                _visibilityRasterPayloads[payloadIndex] = source;
                _visibilityRasterPayloadWrites[payloadIndex] = 1;
                continue;
            }
            AdvancedSceneGeometryOffsets localOffsets =
                source.GeometryOffsets with
                {
                    VertexOffset = 0u,
                    PreviousVertexOffset = 0u,
                    IndexOffset = 0u,
                };
            AdvancedVisibilityPayload local = source with
            {
                GeometryOffsets = localOffsets,
                FirstIndex = 0u,
                IndexCount = primitive.ElementCount,
            };
            if (_visibilityRasterPayloadWrites[payloadIndex] != 0 &&
                _visibilityRasterPayloads[payloadIndex] != local)
            {
                reason = "one canonical payload resolves to conflicting native-local indexed arguments";
                return false;
            }
            _visibilityRasterPayloads[payloadIndex] = local;
            _visibilityRasterPayloadWrites[payloadIndex] = 1;
        }

        rasterPayloads =
            _visibilityRasterPayloads.AsSpan(0, sourcePayloads.Length);
        reason = "Ready";
        return true;
    }

    private VulkanResidentDrawTemplateNativeState ResolveVisibilityNativeState(
        int recordIndex)
    {
        VulkanPreparedStableBinRecord record = _records[recordIndex];
        return _retainedTemplates[recordIndex] is
            VulkanResidentDrawTemplate template
                ? template.NativeState
                : record.VisibilityNativeState;
    }

    /// <summary>
    /// Replaces each sealed range owner's ordinary material pipeline with an
    /// exact visibility program/pipeline closure. All members of the range
    /// must share one indexed geometry binding; mismatches reject the family
    /// before command recording begins. Readiness, the raster program and the
    /// prepared pipeline depend only on a header's coverage, meshlet mode and
    /// cull class plus this call's one target closure, so each distinct
    /// combination is resolved once per call and propagated to every header
    /// that shares it; a header whose pipeline survived from an earlier attempt
    /// is reused only while its program link generation is unchanged.
    /// </summary>
    internal VulkanAdvancedVisibilityPipelineReadiness TryPrepareVisibilityRasterPipelines(
        VulkanAdvancedVisibilityPipelineRuntime visibilityPipelines,
        ReadOnlySpan<AdvancedVisibilityPayload> payloads,
        in VulkanAdvancedVisibilityTargetClosure target,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(visibilityPipelines);
        if (!_submissionPlansSealed || !target.IsValid ||
            _retainedTemplateCount != _recordCount)
        {
            reason = "stable submission plans, target closure, or resident-template leases are unavailable";
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }

        _rasterPipelineScratchValid.AsSpan().Clear();
        bool multiview = target.DynamicRenderingFormats.ViewMask != 0u;
        for (int headerIndex = 0; headerIndex < _headerCount; ++headerIndex)
        {
            VulkanPreparedStableBinHeader header = _headers[headerIndex];
            if (!header.HasSealedSubmission)
                continue;
            if (header.RasterPipeline.IsValid)
            {
                if (header.RasterPipeline.TargetClosure != target)
                {
                    reason = "one accepted stable-bin stream cannot target multiple visibility framebuffer closures";
                    return VulkanAdvancedVisibilityPipelineReadiness.Failed;
                }
                if (header.RasterPipeline.ProgramLinkGeneration ==
                    header.RasterPipeline.Program.LinkGeneration)
                {
                    continue;
                }
                // The program relinked after this header was prepared (a retry
                // across a shader reload): prepare it again below.
            }
            if (header.RecordCount <= 0)
            {
                reason = "a sealed visibility range has no geometry records";
                return VulkanAdvancedVisibilityPipelineReadiness.Failed;
            }

            bool canonicalAtlas =
                !_records[header.RecordOffset].Template.IsValid;
            VulkanResidentDrawTemplateNativeState native =
                ResolveVisibilityNativeState(header.RecordOffset);
            bool meshlet = header.SubmissionPlan!.ResolvedStrategy is
                EMeshSubmissionStrategy.GpuMeshletZeroReadback or
                EMeshSubmissionStrategy.GpuMeshletInstrumented;
            if (native.PrimitiveCount != 1 || native.Primitive0.Topology !=
                PrimitiveTopology.TriangleList || (!meshlet &&
                (!native.Primitive0.Indexed || native.Primitive0.IndexBuffer.Handle == 0)))
            {
                reason = "visibility raster requires one exact triangle-list primitive per range";
                return VulkanAdvancedVisibilityPipelineReadiness.Failed;
            }

            EAdvancedMaterialCoverageMode coverage = header.IndirectRange.Key.Coverage;
            uint cullMode = header.IndirectRange.Key.CullMode;
            int scratchIndex = ResolveRasterPipelineScratchIndex(coverage, meshlet, cullMode);
            VulkanVisibilityRasterPipeline raster;
            if (scratchIndex >= 0 && _rasterPipelineScratchValid[scratchIndex])
            {
                raster = _rasterPipelineScratch[scratchIndex];
            }
            else
            {
                S13aPublicationTelemetry.StepProbe programProbe =
                    S13aPublicationTelemetry.BeginAdvancedFamilyStep();
                VulkanAdvancedVisibilityPipelineReadiness rasterReadiness =
                    visibilityPipelines.TryGetRasterProgram(
                        coverage,
                        meshlet,
                        out VkRenderProgram program,
                        out reason,
                        multiview);
                programProbe.End(S13aAdvancedFamilyStep.RasterProgramLookup);
                bool rasterPrepared = false;
                raster = default;
                if (rasterReadiness == VulkanAdvancedVisibilityPipelineReadiness.Ready)
                {
                    S13aPublicationTelemetry.StepProbe factoryProbe =
                        S13aPublicationTelemetry.BeginAdvancedFamilyStep();
                    rasterPrepared = VulkanCanonicalVisibilityPipelineFactory.TryPrepare(
                        program,
                        coverage,
                        meshlet,
                        cullMode,
                        in target,
                        out raster,
                        out reason);
                    factoryProbe.End(S13aAdvancedFamilyStep.RasterPipelineFactory);
                }
                if (rasterReadiness != VulkanAdvancedVisibilityPipelineReadiness.Ready ||
                    !rasterPrepared)
                {
                    return rasterReadiness != VulkanAdvancedVisibilityPipelineReadiness.Ready
                        ? rasterReadiness
                        : VulkanAdvancedVisibilityPipelineReadiness.Failed;
                }
                if (scratchIndex >= 0)
                {
                    _rasterPipelineScratch[scratchIndex] = raster;
                    _rasterPipelineScratchValid[scratchIndex] = true;
                }
            }

            S13aPublicationTelemetry.StepProbe validationProbe =
                S13aPublicationTelemetry.BeginAdvancedFamilyStep();
            int recordEnd = header.RecordOffset + header.RecordCount;
            for (int recordIndex = header.RecordOffset;
                 recordIndex < recordEnd;
                 ++recordIndex)
            {
                VulkanPreparedStableBinRecord record = _records[recordIndex];
                if ((uint)record.VisibilityPayloadIndex >= (uint)payloads.Length ||
                    payloads[record.VisibilityPayloadIndex].Geometry !=
                        header.IndirectRange.Key.Geometry)
                {
                    continue;
                }
                VulkanResidentDrawTemplateNativeState member =
                    ResolveVisibilityNativeState(recordIndex);
                if (member.PrimitiveCount != 1 ||
                    member.Primitive0.IndexBuffer.Handle !=
                        native.Primitive0.IndexBuffer.Handle ||
                    member.Primitive0.IndexType !=
                        native.Primitive0.IndexType ||
                    member.Primitive0.Topology !=
                        native.Primitive0.Topology ||
                    member.VertexBindingSignature !=
                        native.VertexBindingSignature)
                {
                    reason = "a visibility range spans incompatible native geometry bindings";
                    return VulkanAdvancedVisibilityPipelineReadiness.Failed;
                }
            }

            PendingMeshDraw drawTemplate = native.DrawTemplate;
            VulkanPreparedMeshPrimitive rasterPrimitive =
                native.Primitive0 with { Pipeline = raster.Pipeline };
            VulkanResidentDrawTemplateNativeState rasterNative = canonicalAtlas
                ? new VulkanResidentDrawTemplateNativeState(
                    raster.PipelineLayout,
                    in rasterPrimitive,
                    native.GetVertexBuffer(0),
                    native.GetVertexBinding(0),
                    native.VertexBindingSignature,
                    in drawTemplate)
                : new VulkanResidentDrawTemplateNativeState(
                    raster.PipelineLayout,
                    in rasterPrimitive,
                    default,
                    default,
                    primitiveCount: 1,
                    native.VertexBuffers,
                    native.VertexBindings,
                    native.VertexBindingSignature,
                    in drawTemplate);
            _headers[headerIndex] = header with
            {
                RasterPipeline = raster,
                NativeState = rasterNative,
            };
            validationProbe.End(S13aAdvancedFamilyStep.RasterHeaderValidation);
        }

        reason = "Ready";
        return VulkanAdvancedVisibilityPipelineReadiness.Ready;
    }

    /// <summary>
    /// Prepares one depth-only directional shadow pipeline per sealed bin header
    /// for the directional shadow lane's atlas-page closure. The visibility
    /// raster must already have sealed the bins and retained their templates;
    /// only CPU-direct bins are admitted because the lane issues the frozen
    /// indexed arguments itself.
    /// </summary>
    internal VulkanAdvancedVisibilityPipelineReadiness TryPrepareDirectionalShadowRasterPipelines(
        VulkanAdvancedVisibilityPipelineRuntime visibilityPipelines,
        in VulkanAdvancedVisibilityTargetClosure target,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(visibilityPipelines);
        if (!_submissionPlansSealed || !target.IsValid ||
            target.Kind != EVulkanAdvancedVisibilityTargetKind.DirectionalShadow ||
            _retainedTemplateCount != _recordCount)
        {
            reason = "stable submission plans, the atlas-page closure, or resident-template leases are unavailable";
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }

        VulkanVisibilityRasterPipeline opaque = default;
        VulkanVisibilityRasterPipeline masked = default;
        for (int headerIndex = 0; headerIndex < _headerCount; ++headerIndex)
        {
            VulkanPreparedStableBinHeader header = _headers[headerIndex];
            if (!header.IsRasterReady)
                continue;
            if (header.SubmissionPlan!.ResolvedStrategy != EMeshSubmissionStrategy.CpuDirect)
            {
                reason = "the directional shadow lane records CPU-direct bins only";
                return VulkanAdvancedVisibilityPipelineReadiness.Failed;
            }
            if (header.ShadowRasterPipeline.IsValid)
            {
                if (header.ShadowRasterPipeline.TargetClosure != target)
                {
                    reason = "one accepted stable-bin stream cannot target multiple atlas-page closures";
                    return VulkanAdvancedVisibilityPipelineReadiness.Failed;
                }
                if (header.ShadowRasterPipeline.ProgramLinkGeneration ==
                    header.ShadowRasterPipeline.Program.LinkGeneration)
                {
                    continue;
                }
            }

            EAdvancedMaterialCoverageMode coverage = header.IndirectRange.Key.Coverage;
            if (coverage is not (EAdvancedMaterialCoverageMode.Opaque or EAdvancedMaterialCoverageMode.Masked))
            {
                reason = $"directional shadow coverage '{coverage}' has no lane program";
                return VulkanAdvancedVisibilityPipelineReadiness.Failed;
            }
            bool maskedCoverage = coverage == EAdvancedMaterialCoverageMode.Masked;
            VulkanVisibilityRasterPipeline shadow = maskedCoverage ? masked : opaque;
            if (!shadow.IsValid)
            {
                VulkanAdvancedVisibilityPipelineReadiness programReadiness =
                    visibilityPipelines.TryGetDirectionalShadowProgram(
                        coverage,
                        out VkRenderProgram program,
                        out reason);
                if (programReadiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
                    return programReadiness;
                if (!VulkanDirectionalShadowPipelineFactory.TryPrepare(
                        program,
                        in target,
                        out shadow,
                        out reason))
                {
                    return VulkanAdvancedVisibilityPipelineReadiness.Failed;
                }
                if (maskedCoverage)
                    masked = shadow;
                else
                    opaque = shadow;
            }

            _headers[headerIndex] = header with { ShadowRasterPipeline = shadow };
        }

        reason = "Ready";
        return VulkanAdvancedVisibilityPipelineReadiness.Ready;
    }

    /// <summary>
    /// Maps a header's raster pipeline inputs to a per-call scratch slot, or -1
    /// for a coverage mode the visibility family does not prepare.
    /// </summary>
    private static int ResolveRasterPipelineScratchIndex(
        EAdvancedMaterialCoverageMode coverage,
        bool meshlet,
        uint cullMode)
    {
        int coverageIndex = coverage switch
        {
            EAdvancedMaterialCoverageMode.Opaque => 0,
            EAdvancedMaterialCoverageMode.Masked => 1,
            _ => -1,
        };
        if (coverageIndex < 0)
            return -1;
        return coverageIndex * 4 + (meshlet ? 2 : 0) + (cullMode == 0u ? 0 : 1);
    }

    private bool TryResolveExactRange(
        in VulkanPreparedStableBinHeader header,
        ReadOnlySpan<int> payloadIndexByIngressIndex,
        ReadOnlySpan<AdvancedIndirectRange> indirectRanges,
        ReadOnlySpan<int> indirectPayloadIndices,
        out int rangeIndex,
        out AdvancedIndirectRange range,
        out VulkanSubmissionPlanRejectionReason rejection)
    {
        rangeIndex = -1;
        range = default;
        rejection = VulkanSubmissionPlanRejectionReason.None;
        if (header.RecordCount <= 0)
        {
            rejection = VulkanSubmissionPlanRejectionReason.IndirectRangeUnresolved;
            return false;
        }

        for (int recordOffset = 0; recordOffset < header.RecordCount; ++recordOffset)
        {
            int ingressIndex = _records[header.RecordOffset + recordOffset].IngressIndex;
            if ((uint)ingressIndex >= (uint)payloadIndexByIngressIndex.Length)
            {
                rejection = VulkanSubmissionPlanRejectionReason.IndirectRangeUnresolved;
                return false;
            }
            int payloadIndex = payloadIndexByIngressIndex[ingressIndex];
            int resolvedRangeIndex = (uint)payloadIndex <
                (uint)_rangeIndexByPayloadScratch.Length
                    ? _rangeIndexByPayloadScratch[payloadIndex]
                    : -1;
            if (resolvedRangeIndex < 0)
            {
                rejection = VulkanSubmissionPlanRejectionReason.IndirectRangeUnresolved;
                return false;
            }
            if (rangeIndex >= 0 && rangeIndex != resolvedRangeIndex)
            {
                rejection = VulkanSubmissionPlanRejectionReason.CompositeIndirectRange;
                return false;
            }
            rangeIndex = resolvedRangeIndex;
        }

        range = indirectRanges[rangeIndex];
        return true;
    }

    private bool TryBuildRangeIndexByPayload(
        ReadOnlySpan<AdvancedIndirectRange> indirectRanges,
        ReadOnlySpan<int> indirectPayloadIndices,
        out VulkanSubmissionPlanRejectionReason rejection)
    {
        _rangeIndexByPayloadScratch.AsSpan().Fill(-1);
        rejection = VulkanSubmissionPlanRejectionReason.None;
        for (int rangeIndex = 0; rangeIndex < indirectRanges.Length; ++rangeIndex)
        {
            AdvancedIndirectRange range = indirectRanges[rangeIndex];
            uint end = range.FirstPayloadIndex + range.PayloadCapacity;
            if (range.FirstPayloadIndex > end ||
                end > (uint)indirectPayloadIndices.Length)
            {
                rejection = VulkanSubmissionPlanRejectionReason.IndirectRangeUnresolved;
                return false;
            }
            for (uint index = range.FirstPayloadIndex; index < end; ++index)
            {
                int payloadIndex = indirectPayloadIndices[checked((int)index)];
                if (payloadIndex < 0 ||
                    !EnsurePayloadCapacity(payloadIndex + 1))
                {
                    rejection = VulkanSubmissionPlanRejectionReason.IndirectRangeUnresolved;
                    return false;
                }
                int current = _rangeIndexByPayloadScratch[payloadIndex];
                if (current >= 0 && current != rangeIndex)
                {
                    rejection = VulkanSubmissionPlanRejectionReason.CompositeIndirectRange;
                    return false;
                }
                _rangeIndexByPayloadScratch[payloadIndex] = rangeIndex;
            }
        }
        return true;
    }

    private static int Compare(
        in VulkanPreparedStableBinRecord left,
        in VulkanPreparedStableBinRecord right)
    {
        int result = left.Key.PassCompatibility.CompareTo(right.Key.PassCompatibility);
        if (result != 0) return result;
        result = left.Key.PipelineVariant.CompareTo(right.Key.PipelineVariant);
        if (result != 0) return result;
        result = left.Key.GeometryPage.CompareTo(right.Key.GeometryPage);
        if (result != 0) return result;
        result = left.Key.ViewMask.CompareTo(right.Key.ViewMask);
        if (result != 0) return result;
        result = left.Template.PrimaryIndex.CompareTo(right.Template.PrimaryIndex);
        return result != 0 ? result : left.IngressIndex.CompareTo(right.IngressIndex);
    }
}
