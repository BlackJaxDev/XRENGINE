using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

// These records deliberately mirror the immutable data consumed by native
// recording.  They are not FrameOps: after Lower completes no producer object
// is retained by a frame plan or a prepared worker.
internal readonly record struct TextureUploadPayload(VulkanImportedTexturePendingUpload Upload);
internal readonly record struct BlitPayload(XRFrameBuffer? InFbo, XRFrameBuffer? OutFbo, int InX, int InY, uint InW, uint InH, int OutX, int OutY, uint OutW, uint OutH, EReadBufferMode ReadBufferMode, bool ColorBit, bool DepthBit, bool StencilBit, bool LinearFilter, bool RequireExactCompatibility);
internal readonly record struct ClearPayload(bool ClearColor, bool ClearDepth, bool ClearStencil, ColorF4 Color, float Depth, uint Stencil, Rect2D Rect);
internal readonly record struct TransformFeedbackPayload(VkTransformFeedback TransformFeedback, EXRTransformFeedbackOperation Operation, XRDataBuffer? CounterBuffer, ulong FeedbackBufferOffset, ulong? FeedbackBufferSize, ulong CounterBufferOffset, uint CounterOffset, uint VertexStride, uint InstanceCount, uint FirstInstance);
internal readonly record struct QueryPayload(VkRenderQuery Query, RenderQueryDescriptor Descriptor, ERenderQueryOperation Operation, PipelineStageFlags2 TimestampStage, uint PointIndex, ReadOnlyMemory<ulong> SourceHandles, Buffer ResultDestination, ulong ResultDestinationOffset, ulong ResultStride, bool IncludeAvailability);
internal readonly record struct MeshDrawPayload(PendingMeshDraw Draw);
internal readonly record struct IndirectDrawPayload(VkDataBuffer IndirectBuffer, VkDataBuffer? ParameterBuffer, VkMeshRenderer MeshRenderer, PendingMeshDraw Draw, uint DrawCount, uint Stride, nuint ByteOffset, nuint CountByteOffset, bool UseCount, VulkanBindlessMaterialDescriptorBinding? BindlessMaterialTextures, VulkanIndirectSecondaryRecordingContract SecondaryRecordingContract);
internal readonly record struct MeshTaskDispatchIndirectCountPayload(VkRenderProgram Program, ulong ProgramLinkGeneration, ComputeDispatchSnapshot ProgramBindingSnapshot, VulkanMeshProducerSnapshot ProducerSnapshot, Pipeline Pipeline, VkDataBuffer IndirectBuffer, VkDataBuffer CountBuffer, uint MaxDrawCount, uint Stride, nuint ByteOffset, nuint CountByteOffset, VulkanBindlessMaterialDescriptorBinding? BindlessMaterialTextures);
internal readonly record struct ComputeDispatchPayload(VkRenderProgram Program, uint GroupsX, uint GroupsY, uint GroupsZ, ComputeDispatchSnapshot Snapshot);
internal readonly record struct ComputeDispatchIndirectPayload(VkRenderProgram Program, ComputeDispatchSnapshot Snapshot, VkDataBuffer ArgumentOwner, Buffer ArgumentBuffer, ulong ArgumentOffset, string Label);
internal readonly record struct BufferCopyPayload(VkDataBuffer SourceOwner, Buffer SourceBuffer, ulong SourceOffset, VkDataBuffer DestinationOwner, Buffer DestinationBuffer, ulong DestinationOffset, ulong ByteCount, bool RequireGpuWriteVisibility, GpuDiagnosticSnapshotReceipt? DiagnosticReceipt, string Label);
internal readonly record struct SubmissionMarkerPayload(
    VulkanTimelineGpuFence Fence,
    string Label,
    int RequiredOperationCount);
internal readonly record struct MemoryBarrierPayload(EMemoryBarrierMask Mask);
internal readonly record struct PublishFramebufferPayload(XRFrameBuffer FrameBuffer);
internal readonly record struct DlssUpscalePayload(NvidiaDlssManager.Native.NativeVulkanSession Session, VulkanStreamlineImage SourceColor, VulkanStreamlineImage Depth, VulkanStreamlineImage Motion, VulkanStreamlineImage OutputColor, VulkanStreamlineImage? Exposure, VulkanUpscaleBridgeDispatchParameters Parameters);
internal readonly record struct DlssFrameGenerationPayload(NvidiaDlssManager.Native.NativeFrameGenerationSession Session, VulkanStreamlineImage Depth, VulkanStreamlineImage Motion, VulkanStreamlineImage HudlessColor, VulkanUpscaleBridgeDispatchParameters Parameters, VulkanStreamlineImage UiColorAndAlpha);
/// <summary>
/// Sealed authoring request plus the exact set-1 frame-slot state admitted at
/// primary preparation. The logical attachment names intentionally remain in
/// <see cref="VulkanAdvancedVisibilityStageRequest"/> until recording can
/// resolve the frozen render-graph generation.
/// </summary>
internal readonly record struct VulkanAdvancedVisibilityOperationPayload(
    VulkanAdvancedVisibilityStageRequest Request,
    VulkanAdvancedVisibilityInputStorage Input,
    VulkanAdvancedVisibilityResourceState State,
    VulkanAdvancedScenePublicationState SceneState,
    VulkanAdvancedVisibilityTargetClosure TargetClosure,
    VulkanAdvancedVisibilityLateTargetClosure? LateTargetClosure,
    VulkanAdvancedNativeComputeClosure? NativeComputeClosure,
    DescriptorSet NativeComputeDescriptorSet,
    VkRenderProgram? BoundsPatchProgram,
    Pipeline BoundsPatchPipeline,
    ulong BoundsPatchLinkGeneration,
    VkRenderProgram? EarlyVisibilityProgram,
    Pipeline EarlyVisibilityPipeline,
    ulong EarlyVisibilityLinkGeneration,
    VkRenderProgram? BuildIndirectProgram,
    Pipeline BuildIndirectPipeline,
    ulong BuildIndirectLinkGeneration,
    VkRenderProgram? BuildDepthPyramidProgram,
    Pipeline BuildDepthPyramidPipeline,
    ulong BuildDepthPyramidLinkGeneration,
    VkRenderProgram? LateVisibilityProgram,
    Pipeline LateVisibilityPipeline,
    ulong LateVisibilityLinkGeneration,
    VulkanAdvancedNativeComputePipelines NativeComputePipelines = default,
    VulkanAdvancedMsaaResolvePipeline MultisampleResolvePipeline = default,
    VkRenderProgram? EarlyIndexedGroupFinalizeProgram = null,
    Pipeline EarlyIndexedGroupFinalizePipeline = default,
    ulong EarlyIndexedGroupFinalizeLinkGeneration = 0UL,
    VkRenderProgram? LateIndexedGroupFinalizeProgram = null,
    Pipeline LateIndexedGroupFinalizePipeline = default,
    ulong LateIndexedGroupFinalizeLinkGeneration = 0UL,
    VulkanAdvancedDirectionalShadowResourceState DirectionalShadowResources = default,
    VkRenderProgram? DirectionalShadowCullProgram = null,
    Pipeline DirectionalShadowCullPipeline = default,
    ulong DirectionalShadowCullLinkGeneration = 0UL,
    VkRenderProgram? DirectionalShadowFinalizeProgram = null,
    Pipeline DirectionalShadowFinalizePipeline = default,
    ulong DirectionalShadowFinalizeLinkGeneration = 0UL);

/// <summary>
/// Per-opcode dense storage owned exclusively by a sealed operation stream.
/// The arrays carry concrete payload records rather than a polymorphic
/// <see cref="FrameOp"/> sidecar.
/// </summary>
internal sealed class FrameOperationPayloadStore
{
    /// <summary>
    /// Initial Advanced visibility operation rows for lanes that author Advanced
    /// work. Each row is large (its request, input, state and closure columns),
    /// and a frame issues one per stage phase, per view for per-view stages and
    /// per deferred shadow cascade group, so demand is tens of rows rather than
    /// the general payload budget.
    /// </summary>
    internal const int AdvancedVisibilityInitialCapacity = 64;

    /// <summary>
    /// Initial rows per opcode kind for fixed-capacity stores. The declared
    /// general, mesh and texture capacities stay the admission maximum, but
    /// most kinds see a handful of operations per frame, so every store
    /// reserving the full budget per kind cost tens of megabytes per frame slot.
    /// </summary>
    internal const int InitialGeneralPayloadCapacity = 64;

    /// <summary>Initial mesh-draw rows for fixed-capacity stores (see <see cref="InitialGeneralPayloadCapacity"/>).</summary>
    internal const int InitialMeshPayloadCapacity = 1024;

    private readonly bool _fixedCapacity;
    private readonly int _generalCapacity;
    private readonly int _meshCapacity;
    private readonly int _textureCapacity;
    private readonly EVulkanAcceptedFrameLane _lane;
    private readonly VulkanAdvancedVisibilityInputCopyTelemetry? _advancedVisibilityInputCopyTelemetry;

    internal TextureUploadPayload[] TextureUploads;
    internal BlitPayload[] Blits;
    internal ClearPayload[] Clears;
    internal TransformFeedbackPayload[] TransformFeedbacks;
    internal QueryPayload[] Queries;
    internal MeshDrawPayload[] MeshDraws;
    internal IndirectDrawPayload[] IndirectDraws;
    internal MeshTaskDispatchIndirectCountPayload[] MeshTasks;
    internal ComputeDispatchPayload[] ComputeDispatches;
    internal ComputeDispatchIndirectPayload[] ComputeDispatchIndirects;
    internal BufferCopyPayload[] BufferCopies;
    internal SubmissionMarkerPayload[] SubmissionMarkers;
    internal MemoryBarrierPayload[] MemoryBarriers;
    internal PublishFramebufferPayload[] PublishedFramebuffers;
    internal DlssUpscalePayload[] DlssUpscales;
    internal DlssFrameGenerationPayload[] DlssFrameGenerations;
    internal VulkanAdvancedVisibilityOperationPayload[] AdvancedVisibilities;
    private VulkanAdvancedDirectionalShadowLaneStorage?[] _advancedDirectionalShadowLanes = [];
    internal VulkanAdvancedVisibilityLateClosureStorage[] AdvancedVisibilityLateClosures;
    internal VulkanAdvancedNativeComputeClosureStorage[] AdvancedVisibilityNativeComputeClosures;
    private readonly int _advancedVisibilityDrawCapacity;
    private readonly int _advancedVisibilityRangeCapacity;
    internal readonly VulkanAdvancedVisibilityInputStorage?[] AdvancedVisibilityInputs;
    // Row-owned sealed binding snapshots for the compute-style opcodes. A row
    // is refilled only when its frame slot is rebuilt after the previous frame
    // retired, so no recording worker still reads the old content.
    private ComputeDispatchSnapshot?[] _sealedMeshDrawSnapshots = [];
    private ComputeDispatchSnapshot?[] _sealedIndirectDrawSnapshots = [];
    private ComputeDispatchSnapshot?[] _sealedMeshTaskSnapshots = [];
    private ComputeDispatchSnapshot?[] _sealedComputeDispatchSnapshots = [];
    private ComputeDispatchSnapshot?[] _sealedComputeDispatchIndirectSnapshots = [];
    private VulkanProgramInterfaceEntry?[] _meshDrawInterfaces = [];
    private VulkanProgramInterfaceEntry?[] _indirectDrawInterfaces = [];
    private IRenderResourceLeaseOwner?[] _indirectDrawAuthoringOwners = [];
    private IRenderResourceLeaseOwner?[] _computeDispatchAuthoringOwners = [];
    private IRenderResourceLeaseOwner?[] _bufferCopyAuthoringOwners = [];
    private VulkanProgramInterfaceEntry?[] _meshTaskInterfaces = [];

    internal FrameOperationPayloadStore()
        : this(
            generalCapacity: 0,
            meshCapacity: 0,
            textureCapacity: 0,
            advancedVisibilityDrawCapacity: 0,
            advancedVisibilityRangeCapacity: 0,
            fixedCapacity: false,
            EVulkanAcceptedFrameLane.MainScene)
    {
    }

    /// <summary>
    /// Allocates the complete payload budget at frame-slot construction time.
    /// A sealed foreground frame may consume this storage, but never grow it.
    /// </summary>
    internal FrameOperationPayloadStore(
        int generalCapacity,
        int meshCapacity,
        int textureCapacity,
        int advancedVisibilityDrawCapacity,
        int advancedVisibilityRangeCapacity,
        bool fixedCapacity,
        EVulkanAcceptedFrameLane lane,
        VulkanAdvancedVisibilityInputCopyTelemetry? advancedVisibilityInputCopyTelemetry = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(generalCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(meshCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(textureCapacity);

        _fixedCapacity = fixedCapacity;
        _lane = lane;
        _advancedVisibilityInputCopyTelemetry = advancedVisibilityInputCopyTelemetry;
        _generalCapacity = generalCapacity;
        _meshCapacity = meshCapacity;
        _textureCapacity = textureCapacity;
        int initialGeneral = Math.Min(generalCapacity, InitialGeneralPayloadCapacity);
        TextureUploads = new TextureUploadPayload[Math.Min(textureCapacity, InitialGeneralPayloadCapacity)];
        Blits = new BlitPayload[initialGeneral];
        Clears = new ClearPayload[initialGeneral];
        TransformFeedbacks = new TransformFeedbackPayload[initialGeneral];
        Queries = new QueryPayload[initialGeneral];
        MeshDraws = new MeshDrawPayload[Math.Min(meshCapacity, InitialMeshPayloadCapacity)];
        IndirectDraws = new IndirectDrawPayload[initialGeneral];
        _indirectDrawAuthoringOwners = new IRenderResourceLeaseOwner?[initialGeneral];
        MeshTasks = new MeshTaskDispatchIndirectCountPayload[initialGeneral];
        ComputeDispatches = new ComputeDispatchPayload[initialGeneral];
        _computeDispatchAuthoringOwners = new IRenderResourceLeaseOwner?[initialGeneral];
        ComputeDispatchIndirects = new ComputeDispatchIndirectPayload[initialGeneral];
        BufferCopies = new BufferCopyPayload[initialGeneral];
        _bufferCopyAuthoringOwners = new IRenderResourceLeaseOwner?[initialGeneral];
        SubmissionMarkers = new SubmissionMarkerPayload[initialGeneral];
        MemoryBarriers = new MemoryBarrierPayload[initialGeneral];
        PublishedFramebuffers = new PublishFramebufferPayload[initialGeneral];
        DlssUpscales = new DlssUpscalePayload[initialGeneral];
        DlssFrameGenerations = new DlssFrameGenerationPayload[initialGeneral];
        // UI/upload lanes reserve no Advanced input rows or ranges, so they
        // reserve no Advanced payloads or closure objects. Scene lanes start at
        // a small row budget and grow at a new high-water mark (see
        // EnsureAdvancedVisibilityCapacity).
        int advancedOperationCapacity =
            advancedVisibilityDrawCapacity == 0 && advancedVisibilityRangeCapacity == 0
                ? 0
                : Math.Min(generalCapacity, AdvancedVisibilityInitialCapacity);
        AdvancedVisibilities = advancedOperationCapacity == 0
            ? Array.Empty<VulkanAdvancedVisibilityOperationPayload>()
            : new VulkanAdvancedVisibilityOperationPayload[advancedOperationCapacity];
        AdvancedVisibilityLateClosures = advancedOperationCapacity == 0
            ? Array.Empty<VulkanAdvancedVisibilityLateClosureStorage>()
            : CreateAdvancedVisibilityLateClosureStorage(advancedOperationCapacity);
        AdvancedVisibilityNativeComputeClosures = advancedOperationCapacity == 0
            ? Array.Empty<VulkanAdvancedNativeComputeClosureStorage>()
            : CreateAdvancedNativeComputeClosureStorage(advancedOperationCapacity);
        _advancedVisibilityDrawCapacity = advancedVisibilityDrawCapacity;
        _advancedVisibilityRangeCapacity = advancedVisibilityRangeCapacity;
        AdvancedVisibilityInputs = new VulkanAdvancedVisibilityInputStorage?[VulkanAdvancedVisibilityOutputCapacity.Maximum];
    }

    /// <summary>
    /// Seals an authoring binding snapshot into the reusable snapshot owned by
    /// one payload row, so lowering does not allocate a new snapshot and its
    /// collections for every compute-style operation of every frame. Immutable
    /// binding artifacts are already detached and are returned unchanged.
    /// </summary>
    internal ComputeDispatchSnapshot SealBindingSnapshot(
        EVulkanPrimaryPlanNodeKind kind,
        int row,
        ComputeDispatchSnapshot source)
    {
        if (source.IsImmutableBindingArtifact)
            return source;

        ComputeDispatchSnapshot sealedSnapshot = kind switch
        {
            EVulkanPrimaryPlanNodeKind.MeshDraw =>
                RentSealedSnapshot(ref _sealedMeshDrawSnapshots, row, MeshDraws.Length),
            EVulkanPrimaryPlanNodeKind.IndirectDraw =>
                RentSealedSnapshot(ref _sealedIndirectDrawSnapshots, row, IndirectDraws.Length),
            EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount =>
                RentSealedSnapshot(ref _sealedMeshTaskSnapshots, row, MeshTasks.Length),
            EVulkanPrimaryPlanNodeKind.ComputeDispatch =>
                RentSealedSnapshot(ref _sealedComputeDispatchSnapshots, row, ComputeDispatches.Length),
            EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect =>
                RentSealedSnapshot(ref _sealedComputeDispatchIndirectSnapshots, row, ComputeDispatchIndirects.Length),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The opcode has no binding snapshot column."),
        };
        sealedSnapshot.CopySealedFrom(source);
        return sealedSnapshot;
    }

    /// <summary>Detaches a draw for this store's payload row, sealing its binding snapshot into the row.</summary>
    internal PendingMeshDraw SealDraw(
        EVulkanPrimaryPlanNodeKind kind,
        int row,
        in PendingMeshDraw draw)
    {
        if (kind == EVulkanPrimaryPlanNodeKind.IndirectDraw &&
            draw.IndexedIndirectAuthoringLease is { } owner)
        {
            if (_indirectDrawAuthoringOwners[row] is not null)
                throw new InvalidOperationException("An indirect draw row still owns an authoring lease.");
            owner.RetainAuthoringUse();
            _indirectDrawAuthoringOwners[row] = owner;
        }
        ref VulkanProgramInterfaceEntry? lease = ref GetDrawInterfaceLease(kind, row);
        ReplaceInterfaceLease(ref lease, draw.PreparedProgram, draw.PreparedProgramLinkGeneration);
        return draw.CreateSealedCopy(
            draw.ProgramBindingSnapshot is { } snapshot
                ? SealBindingSnapshot(kind, row, snapshot)
                : null);
    }

    internal void RetainComputeDispatchAuthoringOwner(
        int row, IRenderResourceLeaseOwner? owner)
    {
        if (owner is null)
            return;
        if (_computeDispatchAuthoringOwners[row] is not null)
            throw new InvalidOperationException("A compute dispatch row still owns an authoring lease.");
        owner.RetainAuthoringUse();
        _computeDispatchAuthoringOwners[row] = owner;
    }

    internal void RetainBufferCopyAuthoringOwner(
        int row, IRenderResourceLeaseOwner? owner)
    {
        if (owner is null)
            return;
        if (_bufferCopyAuthoringOwners[row] is not null)
            throw new InvalidOperationException("A buffer copy row still owns an authoring lease.");
        owner.RetainAuthoringUse();
        _bufferCopyAuthoringOwners[row] = owner;
    }

    private ref VulkanProgramInterfaceEntry? GetDrawInterfaceLease(
        EVulkanPrimaryPlanNodeKind kind,
        int row)
    {
        if (kind == EVulkanPrimaryPlanNodeKind.MeshDraw)
            return ref GetInterfaceLease(ref _meshDrawInterfaces, row, MeshDraws.Length);
        if (kind == EVulkanPrimaryPlanNodeKind.IndirectDraw)
            return ref GetInterfaceLease(ref _indirectDrawInterfaces, row, IndirectDraws.Length);
        throw new ArgumentOutOfRangeException(nameof(kind), kind, "The opcode has no draw interface row.");
    }

    internal void SealMeshTaskInterface(int row, VkRenderProgram program, ulong linkGeneration)
    {
        ref VulkanProgramInterfaceEntry? lease = ref GetInterfaceLease(
            ref _meshTaskInterfaces, row, MeshTasks.Length);
        ReplaceInterfaceLease(ref lease, program, linkGeneration);
    }

    private static ref VulkanProgramInterfaceEntry? GetInterfaceLease(
        ref VulkanProgramInterfaceEntry?[] leases,
        int row,
        int columnLength)
    {
        if (row >= leases.Length)
            Array.Resize(ref leases, Math.Max(row + 1, columnLength));
        return ref leases[row];
    }

    private static void ReplaceInterfaceLease(
        ref VulkanProgramInterfaceEntry? owned,
        VkRenderProgram? program,
        ulong linkGeneration)
    {
        VulkanProgramInterfaceEntry? successor = null;
        if (program is not null && linkGeneration != 0UL &&
            !program.TryRetainProgramInterface(linkGeneration, out successor))
            throw new VulkanPlanPreconditionException("The draw's linked Vulkan interface changed before frame sealing.");
        VulkanProgramInterfaceEntry? previous = owned;
        owned = successor;
        if (previous is not null)
            previous.Context.Resources.ProgramInterfaces.Release(previous);
    }

    /// <summary>
    /// Returns the row's reusable snapshot, growing the pool to the payload
    /// column length at a new high-water mark.
    /// </summary>
    private static ComputeDispatchSnapshot RentSealedSnapshot(
        ref ComputeDispatchSnapshot?[] pool,
        int row,
        int columnLength)
    {
        if (row >= pool.Length)
            Array.Resize(ref pool, Math.Max(row + 1, columnLength));
        return pool[row] ??= new ComputeDispatchSnapshot();
    }

    internal void ProvisionAdvancedVisibilityFamily(int bankIndex)
    {
        if (Volatile.Read(ref AdvancedVisibilityInputs[bankIndex]) is null)
            Volatile.Write(ref AdvancedVisibilityInputs[bankIndex], new(
                _advancedVisibilityDrawCapacity,
                _advancedVisibilityRangeCapacity,
                _fixedCapacity,
                _lane,
                _advancedVisibilityInputCopyTelemetry));
    }

    internal VulkanAdvancedVisibilityInputStorage CaptureAdvancedVisibilityInput(
        in VulkanAdvancedVisibilityStageRequest request,
        VulkanAdvancedVisibilityInputStorage authoringInput)
    {
        VulkanAdvancedVisibilityInputStorage? empty = null;
        for (int index = 0; index < AdvancedVisibilityInputs.Length; index++)
        {
            VulkanAdvancedVisibilityInputStorage? input = Volatile.Read(ref AdvancedVisibilityInputs[index]);
            if (input is null)
                continue;
            if (!input.HasCapturedFamily)
            {
                empty ??= input;
                continue;
            }
            if (input.Reservation.OutputId != request.Reservation.OutputId)
                continue;
            // Same-output duplicates must match the entire frozen family. They
            // cannot consume another bank to bypass publication consistency.
            input.CaptureOrValidate(in request, authoringInput);
            return input;
        }
        if (empty is null)
            throw CreateAdvancedInputCapacityFailure(in request);
        empty.CaptureOrValidate(in request, authoringInput);
        return empty;
    }

    internal VulkanAdvancedDirectionalShadowLaneStorage CaptureDirectionalShadowLane(
        int row,
        VulkanAdvancedDirectionalShadowLaneStorage source)
    {
        if ((uint)row >= (uint)_advancedDirectionalShadowLanes.Length)
            Array.Resize(ref _advancedDirectionalShadowLanes,
                Math.Max(row + 1, AdvancedVisibilities.Length));
        VulkanAdvancedDirectionalShadowLaneStorage destination =
            _advancedDirectionalShadowLanes[row] ??= new VulkanAdvancedDirectionalShadowLaneStorage();
        if (!destination.TryCopyFrom(source))
            throw new VulkanPlanPreconditionException(
                "The authored directional shadow lane has no stable physical copy.");
        return destination;
    }

    internal void ClearDirectionalShadowLanes()
    {
        for (int index = 0; index < _advancedDirectionalShadowLanes.Length; ++index)
            _advancedDirectionalShadowLanes[index]?.Clear();
    }

    private VulkanPlanPreconditionException CreateAdvancedInputCapacityFailure(
        in VulkanAdvancedVisibilityStageRequest request)
    {
        // Failure-only diagnostics distinguish a missing cold provision from true
        // output pressure. The successful frame path performs no string allocation.
        var detail = new System.Text.StringBuilder(512);
        detail.Append("The bounded Advanced input-family capacity is exhausted. lane=").Append(_lane)
            .Append(" fixed=").Append(_fixedCapacity)
            .Append(" drawCapacity=").Append(_advancedVisibilityDrawCapacity)
            .Append(" rangeCapacity=").Append(_advancedVisibilityRangeCapacity)
            .Append(" stage=").Append(request.Stage)
            .Append(" requested=").Append(request.Reservation).Append(" banks=[");
        for (int index = 0; index < AdvancedVisibilityInputs.Length; ++index)
        {
            if (index > 0)
                detail.Append(';');
            detail.Append(index).Append(':');
            var input = Volatile.Read(ref AdvancedVisibilityInputs[index]);
            if (input is null)
                detail.Append("unprovisioned");
            else if (!input.HasCapturedFamily)
                detail.Append("empty");
            else
                detail.Append(input.Reservation);
        }
        return new VulkanPlanPreconditionException(detail.Append(']').ToString());
    }

    internal void EnsureCapacity(EVulkanPrimaryPlanNodeKind kind, int count)
    {
        switch (kind)
        {
            case EVulkanPrimaryPlanNodeKind.TextureUpload: Ensure(ref TextureUploads, count, _textureCapacity); break;
            case EVulkanPrimaryPlanNodeKind.Blit: Ensure(ref Blits, count); break;
            case EVulkanPrimaryPlanNodeKind.Clear: Ensure(ref Clears, count); break;
            case EVulkanPrimaryPlanNodeKind.TransformFeedback: Ensure(ref TransformFeedbacks, count); break;
            case EVulkanPrimaryPlanNodeKind.Query: Ensure(ref Queries, count); break;
            case EVulkanPrimaryPlanNodeKind.MeshDraw: Ensure(ref MeshDraws, count, _meshCapacity); break;
            case EVulkanPrimaryPlanNodeKind.IndirectDraw:
                Ensure(ref IndirectDraws, count);
                Ensure(ref _indirectDrawAuthoringOwners, count);
                break;
            case EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount: Ensure(ref MeshTasks, count); break;
            case EVulkanPrimaryPlanNodeKind.ComputeDispatch:
                Ensure(ref ComputeDispatches, count);
                Ensure(ref _computeDispatchAuthoringOwners, count);
                break;
            case EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect: Ensure(ref ComputeDispatchIndirects, count); break;
            case EVulkanPrimaryPlanNodeKind.BufferCopy:
                Ensure(ref BufferCopies, count);
                Ensure(ref _bufferCopyAuthoringOwners, count);
                break;
            case EVulkanPrimaryPlanNodeKind.SubmissionMarker: Ensure(ref SubmissionMarkers, count); break;
            case EVulkanPrimaryPlanNodeKind.MemoryBarrier: Ensure(ref MemoryBarriers, count); break;
            case EVulkanPrimaryPlanNodeKind.PublishFramebufferForSampling: Ensure(ref PublishedFramebuffers, count); break;
            case EVulkanPrimaryPlanNodeKind.DlssUpscale: Ensure(ref DlssUpscales, count); break;
            case EVulkanPrimaryPlanNodeKind.DlssFrameGeneration: Ensure(ref DlssFrameGenerations, count); break;
            case EVulkanPrimaryPlanNodeKind.AdvancedVisibility:
                EnsureAdvancedVisibilityCapacity(count);
                break;
        }
    }

    /// <summary>
    /// Releases immutable storage leases owned by this physical payload store.
    /// Logical OpenXR streams can share this store and must not invoke this
    /// during header-only resets.
    /// </summary>
    internal void ReleaseReadOnlyStorageBindings()
    {
        for (int index = 0; index < _indirectDrawAuthoringOwners.Length; ++index)
        {
            IRenderResourceLeaseOwner? owner = _indirectDrawAuthoringOwners[index];
            _indirectDrawAuthoringOwners[index] = null;
            owner?.ReleaseAuthoringUse();
        }
        for (int index = 0; index < _computeDispatchAuthoringOwners.Length; ++index)
        {
            IRenderResourceLeaseOwner? owner = _computeDispatchAuthoringOwners[index];
            _computeDispatchAuthoringOwners[index] = null;
            owner?.ReleaseAuthoringUse();
        }
        for (int index = 0; index < _bufferCopyAuthoringOwners.Length; ++index)
        {
            IRenderResourceLeaseOwner? owner = _bufferCopyAuthoringOwners[index];
            _bufferCopyAuthoringOwners[index] = null;
            owner?.ReleaseAuthoringUse();
        }
        ReleaseInterfaces(_meshDrawInterfaces);
        ReleaseInterfaces(_indirectDrawInterfaces);
        ReleaseInterfaces(_meshTaskInterfaces);
        for (int index = 0; index < MeshDraws.Length; ++index)
            MeshDraws[index].Draw.ProgramBindingSnapshot?.ReleaseReadOnlyStorageBindings();
        for (int index = 0; index < IndirectDraws.Length; ++index)
            IndirectDraws[index].Draw.ProgramBindingSnapshot?.ReleaseReadOnlyStorageBindings();
        for (int index = 0; index < MeshTasks.Length; ++index)
            MeshTasks[index].ProgramBindingSnapshot?.ReleaseReadOnlyStorageBindings();
        for (int index = 0; index < ComputeDispatches.Length; ++index)
            ComputeDispatches[index].Snapshot?.ReleaseReadOnlyStorageBindings();
        for (int index = 0; index < ComputeDispatchIndirects.Length; ++index)
            ComputeDispatchIndirects[index].Snapshot?.ReleaseReadOnlyStorageBindings();
        for (int index = 0; index < AdvancedVisibilityNativeComputeClosures.Length; ++index)
            AdvancedVisibilityNativeComputeClosures[index].ReleaseAcquiredViews();
        for (int index = 0; index < IndirectDraws.Length; ++index)
        {
            IndirectDrawPayload payload = IndirectDraws[index];
            payload.BindlessMaterialTextures?.Dispose();
            IndirectDraws[index] = payload with { BindlessMaterialTextures = null };
        }
        for (int index = 0; index < MeshTasks.Length; ++index)
        {
            MeshTaskDispatchIndirectCountPayload payload = MeshTasks[index];
            payload.BindlessMaterialTextures?.Dispose();
            MeshTasks[index] = payload with { BindlessMaterialTextures = null };
        }
    }

    private static void ReleaseInterfaces(VulkanProgramInterfaceEntry?[] entries)
    {
        for (int index = 0; index < entries.Length; ++index)
        {
            VulkanProgramInterfaceEntry? entry = entries[index];
            if (entry is null)
                continue;
            entries[index] = null;
            entry.Context.Resources.ProgramInterfaces.Release(entry);
        }
    }

    /// <summary>
    /// Grows one opcode column to hold <paramref name="count"/> rows. A
    /// fixed-capacity store grows only up to its declared capacity for the kind
    /// (<paramref name="maximum"/>, the general capacity by default) and only
    /// when a frame exceeds the previous high-water mark. Growth happens while
    /// the plan is built, before sealing, and a slot is rebuilt only after its
    /// previous frame retired, so no reader observes the replaced array.
    /// </summary>
    private void Ensure<T>(ref T[] values, int count, int maximum = -1)
    {
        if (values.Length >= count)
            return;
        if (maximum < 0)
            maximum = _generalCapacity;
        if (_fixedCapacity && count > maximum)
            throw new VulkanAcceptedFramePlanCapacityException(
                _lane,
                maximum,
                count);

        int grown = Math.Max(count, values.Length == 0 ? 4 : values.Length * 2);
        Array.Resize(ref values, _fixedCapacity ? Math.Min(grown, maximum) : grown);
    }

    /// <summary>
    /// Grows the Advanced visibility rows and their closure storage when a frame
    /// exceeds the previous high-water mark, up to the general capacity on
    /// fixed-capacity lanes (the same rule as <see cref="Ensure{T}"/>). Closure
    /// objects already created are carried over. Lanes that reserve no Advanced
    /// inputs still reject Advanced work.
    /// </summary>
    private void EnsureAdvancedVisibilityCapacity(int count)
    {
        if (AdvancedVisibilities.Length >= count)
            return;
        bool reservesAdvanced = _advancedVisibilityDrawCapacity != 0 || _advancedVisibilityRangeCapacity != 0;
        if (_fixedCapacity && (!reservesAdvanced || count > _generalCapacity))
            throw new VulkanAcceptedFramePlanCapacityException(
                _lane,
                reservesAdvanced ? _generalCapacity : AdvancedVisibilities.Length,
                count);

        int capacity = Math.Max(count, AdvancedVisibilities.Length == 0 ? 4 : AdvancedVisibilities.Length * 2);
        if (_fixedCapacity)
            capacity = Math.Min(capacity, _generalCapacity);
        Array.Resize(ref AdvancedVisibilities, capacity);
        GrowAdvancedVisibilityLateClosures(capacity);
        GrowAdvancedNativeComputeClosures(capacity);
    }

    private void GrowAdvancedVisibilityLateClosures(int capacity)
    {
        if (AdvancedVisibilityLateClosures.Length >= capacity)
            return;

        int previousLength = AdvancedVisibilityLateClosures.Length;
        Array.Resize(ref AdvancedVisibilityLateClosures, capacity);
        for (int index = previousLength;
             index < AdvancedVisibilityLateClosures.Length;
             ++index)
        {
            AdvancedVisibilityLateClosures[index] = new();
        }
    }

    private static VulkanAdvancedVisibilityLateClosureStorage[]
        CreateAdvancedVisibilityLateClosureStorage(int capacity)
    {
        VulkanAdvancedVisibilityLateClosureStorage[] storage =
            new VulkanAdvancedVisibilityLateClosureStorage[capacity];
        for (int index = 0; index < storage.Length; ++index)
            storage[index] = new();
        return storage;
    }

    private void GrowAdvancedNativeComputeClosures(int capacity)
    {
        if (AdvancedVisibilityNativeComputeClosures.Length >= capacity)
            return;

        int previousLength = AdvancedVisibilityNativeComputeClosures.Length;
        Array.Resize(ref AdvancedVisibilityNativeComputeClosures, capacity);
        for (int index = previousLength;
             index < AdvancedVisibilityNativeComputeClosures.Length;
             ++index)
        {
            AdvancedVisibilityNativeComputeClosures[index] = new();
        }
    }

    private static VulkanAdvancedNativeComputeClosureStorage[]
        CreateAdvancedNativeComputeClosureStorage(int capacity)
    {
        VulkanAdvancedNativeComputeClosureStorage[] storage =
            new VulkanAdvancedNativeComputeClosureStorage[capacity];
        for (int index = 0; index < storage.Length; ++index)
            storage[index] = new();
        return storage;
    }
}
