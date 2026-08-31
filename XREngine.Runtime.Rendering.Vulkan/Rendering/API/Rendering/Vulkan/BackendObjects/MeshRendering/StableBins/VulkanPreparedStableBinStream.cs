using XREngine.Rendering.Diagnostics;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using Silk.NET.Vulkan;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Fixed-capacity current-frame stable-bin stream. It is built only after
/// context coalescing and ingress finalization, then frozen before recording.
/// This separates retained topology from frame-local visibility, payload, and
/// late resource uses.
/// </summary>
internal sealed class VulkanPreparedStableBinStream
{
    private readonly VulkanPreparedStableBinRecord[] _records;
    private readonly VulkanPreparedStableBinHeader[] _headers;
    private readonly VulkanSealedBinSubmissionPlan?[] _sealScratchPlans;
    private readonly byte[] _sealScratchPlanAssigned;
    private readonly AdvancedIndirectRange[] _sealScratchRanges;
    private readonly VulkanSealedBinExceptionSnapshot _sealedExceptions;
    private readonly int[] _payloadIndexByIngressScratch;
    private readonly int[] _rangeIndexByPayloadScratch;
    private readonly VulkanTemplateResourceManifest[] _manifestTemplates;
    private readonly VulkanTemplateResourceManifest[] _visibilityAtlasManifests;
    private readonly VulkanCpuIndirectParityArtifact _cpuIndirectParity;
    private readonly VulkanResidentDrawTemplate?[] _retainedTemplates;
    private readonly AdvancedVisibilityPayload[] _visibilityRasterPayloads;
    private readonly byte[] _visibilityRasterPayloadWrites;
    private readonly FrameOpResourceUse[] _lateResourceUses;
    private readonly VulkanBinOrderedExceptionStream _exceptions;
    private int _recordCount;
    private int _lateResourceUseCount;
    private int _headerCount;
    private bool _frozen;
    private bool _submissionPlansSealed;
    private int _retainedTemplateCount;

    internal VulkanPreparedStableBinStream(int capacity, int resourceUseCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfNegative(resourceUseCapacity);
        _records = new VulkanPreparedStableBinRecord[capacity];
        _headers = new VulkanPreparedStableBinHeader[capacity];
        _sealScratchPlans = new VulkanSealedBinSubmissionPlan?[capacity];
        _sealScratchPlanAssigned = new byte[capacity];
        for (int index = 0; index < capacity; ++index)
            _sealScratchPlans[index] = new VulkanSealedBinSubmissionPlan();
        _sealScratchRanges = new AdvancedIndirectRange[capacity];
        _sealedExceptions = new VulkanSealedBinExceptionSnapshot(capacity);
        _payloadIndexByIngressScratch = new int[capacity];
        _rangeIndexByPayloadScratch = new int[capacity];
        _manifestTemplates = new VulkanTemplateResourceManifest[capacity];
        _visibilityAtlasManifests = new VulkanTemplateResourceManifest[capacity];
        for (int index = 0; index < capacity; ++index)
            _visibilityAtlasManifests[index] =
                new VulkanTemplateResourceManifest(3, 3);
        _cpuIndirectParity = new VulkanCpuIndirectParityArtifact(capacity);
        _retainedTemplates = new VulkanResidentDrawTemplate?[capacity];
        _visibilityRasterPayloads = new AdvancedVisibilityPayload[capacity];
        _visibilityRasterPayloadWrites = new byte[capacity];
        _lateResourceUses = new FrameOpResourceUse[resourceUseCapacity];
        _exceptions = new VulkanBinOrderedExceptionStream(capacity);
    }

    internal int RecordCount => _recordCount;
    internal int LateResourceUseCount => _lateResourceUseCount;
    internal int HeaderCount => _headerCount;
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

        if (payloads.IsEmpty)
        {
            reason = "the canonical visibility payload column is empty";
            return false;
        }

        ThawForReuse();
        for (int payloadIndex = 0; payloadIndex < payloads.Length; ++payloadIndex)
        {
            AdvancedVisibilityPayload payload = payloads[payloadIndex];
            if (!payload.Draw.IsValid)
            {
                // The canonical submission sidecar is compact, but keep this
                // guard so a malformed row cannot manufacture a raster bin.
                continue;
            }
            AdvancedGeometryRecord geometry = default;
            bool geometryResolved =
                payload.Geometry.IsValid &&
                publication.Geometry.TryGet(
                    payload.Geometry,
                    out geometry);
            if (!geometryResolved)
            {
                bool drawResolved = publication.Draws.TryGet(
                    payload.Draw,
                    out AdvancedDrawRecord canonicalDraw);
                reason =
                    $"canonical visibility payload {payloadIndex} has no immutable geometry association " +
                    $"(submissionSequence={publication.Submission.Sequence}, " +
                    $"draw={payload.Draw.Index}:{payload.Draw.Generation}, " +
                    $"drawResolved={drawResolved}, " +
                    $"drawGeometry={canonicalDraw.Geometry.Index}:{canonicalDraw.Geometry.Generation}, " +
                    $"geometry={payload.Geometry.Index}:{payload.Geometry.Generation}, " +
                    $"geometrySnapshotSequence={publication.Geometry.Sequence}, " +
                    $"geometryRecordImage={publication.Geometry.HasRecordImage}, " +
                    $"geometryRecordCount={publication.Geometry.RecordCount}, " +
                    $"geometryPhysicalHighWater={publication.Geometry.PhysicalRecords.Length}, " +
                    $"geometryLookupCount={publication.Geometry.HandleLookups.Length})";
                ThawForReuse();
                return false;
            }

            if ((payload.Skinned && geometry.Source !=
                    EAdvancedGeometrySource.PreSkinnedCurrentAndPrevious) ||
                (!payload.Skinned && geometry.Source is not (
                    EAdvancedGeometrySource.Static or
                    EAdvancedGeometrySource.MeshletLocal)) ||
                !geometry.CurrentVertexData.IsValid ||
                !geometry.IndexData.IsValid ||
                geometry.CurrentVertexData.ElementStride != 64u ||
                geometry.IndexData.ElementStride != sizeof(uint) ||
                payload.GeometryOffsets.VertexOffset != geometry.VertexBase ||
                payload.FirstIndex != geometry.IndexBase ||
                payload.IndexCount != geometry.IndexCount ||
                payload.VertexCount != geometry.VertexCount)
            {
                reason = $"canonical visibility payload {payloadIndex} does not match its immutable packed geometry range";
                ThawForReuse();
                return false;
            }

            VulkanFrameDataSlice vertexSlice = payload.Skinned
                ? sceneState.PreSkinnedCurrent
                : sceneState.StaticVertices;
            VulkanVisibilityGeometryRecordClosure geometryClosure = new(
                payload.Geometry,
                geometry.Source,
                vertexSlice,
                sceneState.Indices,
                geometry.CurrentVertexData,
                geometry.IndexData,
                geometry.VertexBase,
                geometry.VertexCount,
                geometry.IndexBase,
                geometry.IndexCount,
                geometry.VertexLayoutId,
                sceneState.NativeGeneration);
            if (!geometryClosure.TryValidate(in sceneState, out reason))
            {
                reason = $"canonical visibility payload {payloadIndex}: {reason}";
                ThawForReuse();
                return false;
            }

            VkBufferHandle vertices = vertexSlice.Buffer;
            VkBufferHandle indices = sceneState.Indices.Buffer;

            ulong vertexSignature = MixVisibilityKey(
                vertices.Handle,
                vertexSlice.Generation,
                vertexSlice.Offset,
                0u);
            PendingMeshDraw draw = default(PendingMeshDraw) with
            {
                Renderer = null!,
                RasterizationSamples = Silk.NET.Vulkan.SampleCountFlags.Count1Bit,
                DepthTestEnabled = true,
                DepthWriteEnabled = true,
                DepthCompareOp = Silk.NET.Vulkan.CompareOp.LessOrEqual,
                CullMode = payload.CullMode == 0u
                    ? Silk.NET.Vulkan.CullModeFlags.None
                    : Silk.NET.Vulkan.CullModeFlags.BackBit,
                FrontFace = Silk.NET.Vulkan.FrontFace.CounterClockwise,
                ColorWriteMask = Silk.NET.Vulkan.ColorComponentFlags.RBit |
                    Silk.NET.Vulkan.ColorComponentFlags.GBit |
                    Silk.NET.Vulkan.ColorComponentFlags.BBit |
                    Silk.NET.Vulkan.ColorComponentFlags.ABit,
                Instances = Math.Max(1u, payload.InstanceCount),
            };
            VulkanPreparedMeshPrimitive primitive = new(
                default,
                Silk.NET.Vulkan.PrimitiveTopology.TriangleList,
                indices,
                Silk.NET.Vulkan.IndexType.Uint32,
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
                vertexSlice,
                sceneState.Indices,
                in native,
                in context);
            VulkanTemplateResourceManifest manifest =
                _visibilityAtlasManifests[payloadIndex];
            manifest.ResetVisibilityGeometry(
                in payload,
                vertices,
                indices);
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
                            geometry.IndexBase,
                            checked((int)geometry.VertexBase),
                            checked((uint)payloadIndex)),
                        payload.Material.Index,
                        payload.Draw.Index,
                        native,
                        geometryClosure),
                    ReadOnlySpan<FrameOpResourceUse>.Empty))
            {
                reason = "the canonical visibility atlas stream exceeded its fixed capacity";
                ThawForReuse();
                return false;
            }
        }

        Freeze();
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
        _cpuIndirectParity.Reset();
        _exceptions.Clear();
    }

    internal bool TryAppend(
        in VulkanPreparedStableBinRecord record,
        ReadOnlySpan<FrameOpResourceUse> lateResourceUses)
    {
        if (_frozen || _recordCount == _records.Length ||
            lateResourceUses.Length > _lateResourceUses.Length - _lateResourceUseCount)
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
        for (int index = 1; index < _recordCount; ++index)
        {
            VulkanPreparedStableBinRecord record = _records[index];
            int insertion = index;
            while (insertion > 0 && Compare(record, _records[insertion - 1]) < 0)
            {
                _records[insertion] = _records[insertion - 1];
                --insertion;
            }
            _records[insertion] = record;
        }
        _headerCount = 0;
        _submissionPlansSealed = false;
        _frozen = true;
        if (VulkanFeatureProfile.ActiveProfile is
            EVulkanGpuDrivenProfile.DevParity or EVulkanGpuDrivenProfile.Diagnostics)
            _ = TryBuildCpuIndirectParity(out _);
    }

    internal void ThawForReuse()
    {
        ReleaseRetainedTemplates();
        _frozen = false;
        _recordCount = 0;
        _lateResourceUseCount = 0;
        _headerCount = 0;
        _submissionPlansSealed = false;
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
        if (source._recordCount > _records.Length ||
            source._lateResourceUseCount > _lateResourceUses.Length ||
            source._exceptions.Count > _exceptions.Capacity)
        {
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.MainScene,
                Math.Min(
                    Math.Min(_records.Length, _lateResourceUses.Length),
                    _exceptions.Capacity),
                Math.Max(
                    Math.Max(source._recordCount, source._lateResourceUseCount),
                    source._exceptions.Count));
        }

        ThawForReuse();
        source.Records.CopyTo(_records);
        for (int recordIndex = 0; recordIndex < source._recordCount; ++recordIndex)
        {
            VulkanPreparedStableBinRecord record = _records[recordIndex];
            if (record.Template.IsValid)
                continue;
            VulkanTemplateResourceManifest manifest =
                _visibilityAtlasManifests[recordIndex];
            manifest.CopyFrom(record.TemplateManifest);
            _records[recordIndex] = record with { TemplateManifest = manifest };
        }
        source.LateResourceUses.CopyTo(_lateResourceUses);
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
            if (header.SubmissionPlan is not { } sourcePlan)
            {
                _sealScratchPlanAssigned[headerIndex] = 0;
                continue;
            }
            VulkanSealedBinSubmissionPlan destinationPlan =
                _sealScratchPlans[headerIndex]!;
            destinationPlan.CopyFrom(sourcePlan, _sealedExceptions);
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
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (!_frozen)
            throw new InvalidOperationException("Stable-bin manifests require a frozen stream.");

        _headerCount = 0;
        _submissionPlansSealed = false;
        for (int start = 0; start < _recordCount;)
        {
            VulkanRenderBinKey key = _records[start].Key;
            int end = start + 1;
            while (end < _recordCount && _records[end].Key == key)
                ++end;

            VulkanBinResourceManifest? manifest;
            if (!cache.TryGet(topologyGeneration, key, out manifest))
            {
                int resourceCapacity = 0;
                int nativeUseCapacity = 0;
                int templateCount = end - start;
                for (int index = 0; index < templateCount; ++index)
                {
                    VulkanTemplateResourceManifest template = _records[start + index].TemplateManifest;
                    _manifestTemplates[index] = template;
                    resourceCapacity = checked(resourceCapacity + template.Count);
                    nativeUseCapacity = checked(nativeUseCapacity + template.NativeUseCount);
                }
                if (!VulkanBinResourceManifest.TryCreate(
                        _manifestTemplates.AsSpan(0, templateCount),
                        resourceCapacity,
                        nativeUseCapacity,
                        out manifest,
                        out _))
                {
                    return false;
                }
                cache.Store(topologyGeneration, key, manifest!);
            }

            if (_headerCount == _headers.Length)
                return false;
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
                    _sealScratchPlans[headerIndex]!,
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
            if ((uint)record.IngressIndex >=
                (uint)_payloadIndexByIngressScratch.Length)
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
        if (!_submissionPlansSealed ||
            _retainedTemplateCount != _recordCount ||
            sourcePayloads.IsEmpty ||
            sourcePayloads.Length > _visibilityRasterPayloads.Length)
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
    /// before command recording begins.
    /// </summary>
    internal bool TryPrepareVisibilityRasterPipelines(
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
            return false;
        }

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
                    return false;
                }
                continue;
            }
            if (header.RecordCount <= 0)
            {
                reason = "a sealed visibility range has no geometry records";
                return false;
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
                return false;
            }
            if (!visibilityPipelines.TryGetRasterProgram(
                    header.IndirectRange.Key.Coverage,
                    meshlet,
                    out VkRenderProgram program,
                    out reason) ||
                !VulkanCanonicalVisibilityPipelineFactory.TryPrepare(
                    program,
                    header.IndirectRange.Key.Coverage,
                    meshlet,
                    header.IndirectRange.Key.CullMode,
                    in target,
                    out VulkanVisibilityRasterPipeline raster,
                    out reason))
            {
                return false;
            }

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
                    return false;
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
        }

        reason = "Ready";
        return true;
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
                if ((uint)payloadIndex >=
                    (uint)_rangeIndexByPayloadScratch.Length)
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
