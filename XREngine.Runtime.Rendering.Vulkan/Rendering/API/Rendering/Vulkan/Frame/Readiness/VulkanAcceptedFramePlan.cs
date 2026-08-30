using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Slot-owned immutable producer snapshot accepted before swapchain acquisition.
/// Authoring operations live here only until the native numeric plan is sealed.
/// All storage is allocated with the arena, never while executing an accepted
/// foreground frame.
/// </summary>
internal sealed class VulkanAcceptedFramePlan
{
    internal const int TerminalCapacity = 512;
    internal const int UiCapacity = 1024;
    internal const int MainSceneCapacity = 8192;
    internal const int ShadowCapacity = 4096;
    internal const int UploadCapacity = 4096;
    internal const int DependencyCapacity = 16384;
    private const int DependencyIndexCapacity = DependencyCapacity * 2;
    private const int DependencyIndexMask = DependencyIndexCapacity - 1;
    internal const int StaticCapacity = TerminalCapacity + MainSceneCapacity + ShadowCapacity;

    private readonly FrameOp[] _staticOperations = new FrameOp[StaticCapacity];
    private readonly FrameOp[] _dynamicUiOperations = new FrameOp[UiCapacity];
    private readonly FrameOp[] _textureUploadOperations = new FrameOp[UploadCapacity];
    private readonly XRTexture?[] _requiredTextures = new XRTexture?[UploadCapacity];
    private readonly long[] _requiredTextureGenerations = new long[UploadCapacity];
    private readonly VulkanBindlessMaterialTextureReceipt[] _bindlessTextureReceipts =
        new VulkanBindlessMaterialTextureReceipt[UploadCapacity];
    private readonly VulkanFrameDependencyTicket[] _dependencies =
        new VulkanFrameDependencyTicket[DependencyCapacity];
    private readonly VulkanTimelineGpuFence?[] _submissionMarkers =
        new VulkanTimelineGpuFence?[StaticCapacity + UiCapacity + UploadCapacity];
    private readonly int[] _dependencyIndex = new int[DependencyIndexCapacity];
    private readonly int[] _dependencyIndexSlots = new int[DependencyCapacity];
    private FramePlan? _logicalPlan;
    private int _dependencyIndexSlotCount;
    private int _submissionMarkerCount;
    private bool _submissionMarkerOwnershipTransferred;
    private VulkanDescriptorManager? _bindlessReceiptLeaseOwner;
    private int _bindlessReceiptCount;

    internal VulkanCanonicalPublicationPinSet CanonicalPublicationPins { get; } =
        new(VulkanMeshOperationRequestQueue.Capacity);
    internal VulkanPreparedMeshIngress PreparedMeshIngress { get; } = new();
    internal VulkanTextureUploadManifest RequiredTextureUploads { get; } = new();
    internal ShadowAtlasReadinessManifest ShadowReadiness { get; set; }
    internal ShadowAtlasReadinessResult ShadowReadinessResult { get; set; }
    internal ulong FrameId { get; private set; }
    internal ulong SceneEpoch { get; private set; }
    internal int FrameSlot { get; private set; } = -1;
    internal int StaticOperationCount { get; private set; }
    internal int DynamicUiOperationCount { get; private set; }
    internal int TextureUploadOperationCount { get; private set; }
    internal int RequiredTextureCount { get; private set; }
    internal int TerminalOperationCount { get; private set; }
    internal int MainSceneOperationCount { get; private set; }
    internal int ShadowOperationCount { get; private set; }
    internal int DependencyCount { get; private set; }
    internal RenderOutputRequest OutputContract { get; private set; }
    internal VulkanPresentNowTargetCompatibilityKey TargetCompatibility { get; private set; }
    internal ulong LogicalPlanGeneration { get; private set; }
    internal ResourcePlannerRuntimeState PlannerState { get; private set; }
    internal VulkanFramePlanningSnapshot FrozenPlanningSnapshot { get; private set; }
    internal bool IsSealed { get; private set; }
    internal FramePlan LogicalPlan => _logicalPlan ??
        throw new InvalidOperationException(
            "The accepted frame has no sealed logical plan publication.");

    internal FrameOp[] StaticOperations => _staticOperations;
    internal FrameOp[] DynamicUiOperations => _dynamicUiOperations;
    internal FrameOp[] TextureUploadOperations => _textureUploadOperations;
    internal ReadOnlySpan<XRTexture?> RequiredTextures
        => _requiredTextures.AsSpan(0, RequiredTextureCount);
    internal ReadOnlySpan<long> RequiredTextureGenerations
        => _requiredTextureGenerations.AsSpan(0, RequiredTextureCount);

    /// <summary>
    /// Claims producer fences as soon as their authoring operations leave the
    /// frame queue. Until native recording succeeds, this plan is solely
    /// responsible for terminalizing them on abort.
    /// </summary>
    internal void ClaimUnsubmittedSubmissionMarkers(
        ReadOnlySpan<FrameOp> operations)
    {
        if (_submissionMarkerOwnershipTransferred)
            throw new InvalidOperationException(
                "Submission-marker ownership has already transferred to a command buffer.");

        for (int index = 0; index < operations.Length; index++)
        {
            if (operations[index] is not SubmissionMarkerOp marker)
                continue;
            if (ContainsSubmissionMarker(marker.Fence))
                continue;
            if (_submissionMarkerCount >= _submissionMarkers.Length)
            {
                SettleUnsubmittedSubmissionMarkers();
                throw new VulkanAcceptedFramePlanCapacityException(
                    EVulkanAcceptedFrameLane.Dependency,
                    _submissionMarkers.Length,
                    _submissionMarkerCount + 1);
            }

            _submissionMarkers[_submissionMarkerCount++] = marker.Fence;
        }
    }

    private bool ContainsSubmissionMarker(VulkanTimelineGpuFence fence)
    {
        for (int index = 0; index < _submissionMarkerCount; index++)
            if (ReferenceEquals(_submissionMarkers[index], fence))
                return true;
        return false;
    }

    /// <summary>
    /// Fails raw producer markers excluded by output admission and keeps only
    /// the exact marker set that native recording can register. The remaining
    /// set transfers to the command buffer after recording succeeds.
    /// </summary>
    private void ReconcileSubmissionMarkersWithSealedPlan(FramePlan logicalPlan)
    {
        int retainedCount = 0;
        for (int index = 0; index < _submissionMarkerCount; index++)
        {
            VulkanTimelineGpuFence? fence = _submissionMarkers[index];
            if (fence is not null && IsSubmissionMarkerAdmitted(logicalPlan, fence))
            {
                _submissionMarkers[retainedCount++] = fence;
                continue;
            }

            fence?.Fail();
        }

        _submissionMarkers.AsSpan(
            retainedCount,
            _submissionMarkerCount - retainedCount).Clear();
        _submissionMarkerCount = retainedCount;
    }

    private static bool IsSubmissionMarkerAdmitted(
        FramePlan logicalPlan,
        VulkanTimelineGpuFence fence)
        => ContainsSubmissionMarker(
                logicalPlan.GetNativeStaticOperationsForRecording(),
                fence) ||
            ContainsSubmissionMarker(
                logicalPlan.GetNativeDynamicOverlayOperationsForRecording(),
                fence) ||
            ContainsSubmissionMarker(
                logicalPlan.GetNativeTextureUploadOperationsForRecording(),
                fence);

    private static bool ContainsSubmissionMarker(
        FrameOperationSequence operations,
        VulkanTimelineGpuFence fence)
    {
        for (int index = 0; index < operations.Length; index++)
        {
            if (operations.GetHeader(index).OpCode ==
                    EVulkanPrimaryPlanNodeKind.SubmissionMarker &&
                ReferenceEquals(
                    operations.GetSubmissionMarker(index).Fence,
                    fence))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Transfers marker settlement to the recorded command buffer. Queue-submit
    /// acceptance or rejection owns the markers after this point.
    /// </summary>
    internal void TransferSubmissionMarkerOwnershipToCommandBuffer()
    {
        if (_submissionMarkerOwnershipTransferred)
            return;

        _submissionMarkers.AsSpan(0, _submissionMarkerCount).Clear();
        _submissionMarkerCount = 0;
        _submissionMarkerOwnershipTransferred = true;
    }

    /// <summary>Fails every marker still owned by this unsubmitted plan once.</summary>
    internal void SettleUnsubmittedSubmissionMarkers()
    {
        if (_submissionMarkerOwnershipTransferred || _submissionMarkerCount == 0)
            return;

        for (int index = 0; index < _submissionMarkerCount; index++)
            _submissionMarkers[index]?.Fail();
        _submissionMarkers.AsSpan(0, _submissionMarkerCount).Clear();
        _submissionMarkerCount = 0;
    }

    internal void Begin(
        int frameSlot,
        ulong frameId,
        ulong sceneEpoch,
        in VulkanPresentNowTargetCompatibilityKey targetCompatibility)
    {
        Reset();
        FrameSlot = frameSlot;
        FrameId = frameId;
        SceneEpoch = sceneEpoch;
        TargetCompatibility = targetCompatibility;
    }

    internal void CaptureOperations(
        ReadOnlySpan<FrameOp> staticOperations,
        ReadOnlySpan<FrameOp> dynamicUiOperations,
        ReadOnlySpan<FrameOp> textureUploadOperations)
    {
        if (IsSealed)
            throw new InvalidOperationException("The accepted frame plan is already sealed.");

        for (int index = 0; index < staticOperations.Length; index++)
        {
            FrameOp operation = staticOperations[index];
            EVulkanAcceptedFrameLane lane = ClassifyStaticOperation(operation);
            int laneCount = lane switch
            {
                EVulkanAcceptedFrameLane.Terminal => ++TerminalOperationCount,
                EVulkanAcceptedFrameLane.Shadow => ++ShadowOperationCount,
                _ => ++MainSceneOperationCount,
            };
            int laneCapacity = lane switch
            {
                EVulkanAcceptedFrameLane.Terminal => TerminalCapacity,
                EVulkanAcceptedFrameLane.Shadow => ShadowCapacity,
                _ => MainSceneCapacity,
            };
            if (laneCount > laneCapacity)
                throw new VulkanAcceptedFramePlanCapacityException(
                    lane,
                    laneCapacity,
                    laneCount);
            if (StaticOperationCount >= _staticOperations.Length)
                throw new VulkanAcceptedFramePlanCapacityException(
                    lane,
                    _staticOperations.Length,
                    StaticOperationCount + 1);
            _staticOperations[StaticOperationCount++] = operation;
        }

        if (dynamicUiOperations.Length > _dynamicUiOperations.Length)
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.Ui,
                _dynamicUiOperations.Length,
                dynamicUiOperations.Length);
        dynamicUiOperations.CopyTo(_dynamicUiOperations);
        DynamicUiOperationCount = dynamicUiOperations.Length;

        if (textureUploadOperations.Length > _textureUploadOperations.Length)
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.Upload,
                _textureUploadOperations.Length,
                textureUploadOperations.Length);
        textureUploadOperations.CopyTo(_textureUploadOperations);
        TextureUploadOperationCount = textureUploadOperations.Length;
    }

    /// <summary>
    /// Freezes texture owners directly from the raw visible mesh cohort before
    /// material-table rows or descriptor snapshots are produced. PresentNow
    /// can therefore finish the selected streaming generations first, and the
    /// later materialization observes those exact resident wrappers.
    /// </summary>
    internal void CaptureRequiredTextureReferences(
        ReadOnlySpan<VulkanMeshRenderRequest> requests)
    {
        if (IsSealed)
            throw new InvalidOperationException(
                "The accepted frame plan is already sealed.");

        for (int index = 0; index < requests.Length; index++)
        {
            ref readonly VulkanMeshRenderRequest request = ref requests[index];
            CaptureMaterialTextureReferences(
                request.ResolvedMaterial.Material,
                includePendingUploadGeneration: true);
            CaptureMaterialTextureReferences(
                request.ResolvedMaterial.ShadowUniformSourceMaterial,
                includePendingUploadGeneration: true);
            CaptureMaterialTextureReferences(
                request.MaterialOverride,
                includePendingUploadGeneration: true);
        }
    }

    /// <summary>
    /// Freezes the exact texture owners referenced by the accepted draw/material
    /// set. Upload readiness may use this list to exclude unrelated VisibleNow
    /// work from the foreground barrier.
    /// </summary>
    internal void CaptureRequiredTextureReferences(
        FramePlan logicalPlan,
        VulkanDescriptorManager descriptorManager)
    {
        ArgumentNullException.ThrowIfNull(logicalPlan);
        ArgumentNullException.ThrowIfNull(descriptorManager);
        if (!logicalPlan.IsSealed)
            throw new VulkanPlanPreconditionException(
                "Texture readiness closure requires a sealed logical plan.");

        bool referencesBindlessTable = false;
        CaptureOperationTextureReferences(
            logicalPlan.GetNativeStaticOperationsForRecording(),
            ref referencesBindlessTable);
        CaptureOperationTextureReferences(
            logicalPlan.GetNativeDynamicOverlayOperationsForRecording(),
            ref referencesBindlessTable);
        CaptureOperationTextureReferences(
            logicalPlan.GetNativeTextureUploadOperationsForRecording(),
            ref referencesBindlessTable);
        if (referencesBindlessTable)
            CaptureBindlessMaterialTextureReferences(descriptorManager);
    }

    private void CaptureOperationTextureReferences(
        FrameOperationSequence operations,
        ref bool referencesBindlessTable)
    {
        for (int index = 0; index < operations.Length; index++)
        {
            switch (operations.GetHeader(index).OpCode)
            {
                case EVulkanPrimaryPlanNodeKind.MeshDraw:
                    ref readonly MeshDrawPayload draw =
                        ref operations.GetMeshDraw(index);
                    PendingMeshDraw meshDraw = draw.Draw;
                    CaptureDrawTextureReferences(in meshDraw);
                    CaptureSnapshotTextureReferences(
                        meshDraw.ProgramBindingSnapshot);
                    break;
                case EVulkanPrimaryPlanNodeKind.IndirectDraw:
                    ref readonly IndirectDrawPayload indirect =
                        ref operations.GetIndirectDraw(index);
                    PendingMeshDraw indirectDraw = indirect.Draw;
                    CaptureDrawTextureReferences(in indirectDraw);
                    CaptureSnapshotTextureReferences(
                        indirectDraw.ProgramBindingSnapshot);
                    referencesBindlessTable |=
                        indirect.BindlessMaterialTextures.HasValue;
                    break;
                case EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount:
                    ref readonly MeshTaskDispatchIndirectCountPayload meshTask =
                        ref operations.GetMeshTask(index);
                    CaptureSnapshotTextureReferences(
                        meshTask.ProgramBindingSnapshot);
                    referencesBindlessTable |=
                        meshTask.BindlessMaterialTextures.HasValue;
                    break;
                case EVulkanPrimaryPlanNodeKind.ComputeDispatch:
                    CaptureSnapshotTextureReferences(
                        operations.GetComputeDispatch(index).Snapshot);
                    break;
                case EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect:
                    CaptureSnapshotTextureReferences(
                        operations.GetComputeDispatchIndirect(index).Snapshot);
                    break;
                case EVulkanPrimaryPlanNodeKind.TextureUpload:
                    ref readonly TextureUploadPayload upload =
                        ref operations.GetTextureUpload(index);
                    if (upload.Upload.TryGetTexture(out XRTexture2D? texture))
                    {
                        AddRequiredTextureReference(
                            texture,
                            upload.Upload.Request.StreamingGeneration);
                    }
                    break;
                case EVulkanPrimaryPlanNodeKind.AdvancedVisibility:
                    CaptureAdvancedVisibilityTextureReferences(
                        operations.GetAdvancedVisibility(index));
                    break;
            }
        }
    }

    private void CaptureAdvancedVisibilityTextureReferences(
        in VulkanAdvancedVisibilityOperationPayload payload)
    {
        if (!payload.Request.BackendPackage.TryGetCurrent(
                out BackendReadyFramePackage package) ||
            !package.TryGetCanonicalPublicationSnapshot(
                out AdvancedGpuScenePublicationSnapshot snapshot))
        {
            return;
        }

        AdvancedGpuResourcePublicationSnapshot resources =
            snapshot.ResourcePayloads;
        ReadOnlySpan<AdvancedGpuHandle> handles =
            resources.TextureSourceHandles;
        for (int index = 0; index < handles.Length; ++index)
        {
            if (resources.TryGetTextureSource(
                    handles[index],
                    out XRTexture source))
            {
                AddRequiredTextureReference(
                    source,
                    includePendingUploadGeneration: true);
            }
        }
    }

    private void CaptureDrawTextureReferences(in PendingMeshDraw draw)
    {
        XRMaterial? material =
            draw.MaterialOverride ?? draw.Renderer.MeshRenderer.Material;
        CaptureMaterialTextureReferences(material);
    }

    private void CaptureMaterialTextureReferences(
        XRMaterial? material,
        bool includePendingUploadGeneration = false)
    {
        if (material is null)
            return;

        for (int index = 0; index < material.Textures.Count; index++)
        {
            AddRequiredTextureReference(
                material.Textures[index],
                includePendingUploadGeneration:
                    includePendingUploadGeneration);
        }
    }

    private void CaptureSnapshotTextureReferences(
        ComputeDispatchSnapshot? snapshot)
    {
        if (snapshot is null)
            return;

        foreach (XRTexture texture in snapshot.Samplers.Values)
            AddRequiredTextureReference(texture);
        foreach (ProgramImageBinding image in snapshot.Images.Values)
            AddRequiredTextureReference(image.Texture);
    }

    private void CaptureBindlessMaterialTextureReferences(
        VulkanDescriptorManager descriptorManager)
    {
        if (_bindlessReceiptCount != 0)
            return;

        if (!descriptorManager.TryAcquireGlobalMaterialTextureReceiptLeases(
                _bindlessTextureReceipts,
                out int count,
                out string reason))
        {
            _bindlessTextureReceipts.AsSpan().Clear();
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.Upload,
                _bindlessTextureReceipts.Length,
                _bindlessTextureReceipts.Length + 1,
                reason);
        }

        _bindlessReceiptLeaseOwner = descriptorManager;
        _bindlessReceiptCount = count;
        try
        {
            for (int index = 0; index < count; index++)
            {
                ref readonly VulkanBindlessMaterialTextureReceipt receipt =
                    ref _bindlessTextureReceipts[index];
                AddRequiredTextureReference(
                    receipt.Texture,
                    receipt.StreamingGeneration);
            }
        }
        catch
        {
            descriptorManager.ReleaseGlobalMaterialTextureReceiptLeases(
                _bindlessTextureReceipts.AsSpan(0, count));
            _bindlessTextureReceipts.AsSpan(0, count).Clear();
            _bindlessReceiptLeaseOwner = null;
            _bindlessReceiptCount = 0;
            throw;
        }
    }

    private void AddRequiredTextureReference(
        XRTexture? texture,
        long explicitGeneration = 0L,
        bool includePendingUploadGeneration = false)
    {
        if (texture is null)
            return;
        long requiredGeneration = explicitGeneration;
        if (requiredGeneration <= 0L && texture is XRTexture2D texture2D &&
            ImportedTextureStreamingManager.Instance.TryGetGenerationState(
                texture2D,
                out long publishedGeneration,
                out long uploadGeneration,
                out _,
                out _))
        {
            // Before materialization, the accepted visible cohort owns the
            // newest requested generation so foreground readiness can finish it
            // before descriptor capture. Once the logical plan is sealed, only
            // a generation that has already published may refine the snapshot.
            // Admitting a newly pending upload at that point would mutate the
            // accepted frame and demand descriptor publication for bindings the
            // sealed plan does not reference.
            requiredGeneration = includePendingUploadGeneration
                ? Math.Max(publishedGeneration, uploadGeneration)
                : publishedGeneration;
        }
        for (int index = 0; index < RequiredTextureCount; index++)
            if (ReferenceEquals(_requiredTextures[index], texture))
            {
                if (requiredGeneration > 0L)
                {
                    _requiredTextureGenerations[index] = Math.Max(
                        _requiredTextureGenerations[index],
                        requiredGeneration);
                }
                return;
            }
        if (RequiredTextureCount >= _requiredTextures.Length)
        {
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.Upload,
                _requiredTextures.Length,
                RequiredTextureCount + 1);
        }

        _requiredTextures[RequiredTextureCount] = texture;
        _requiredTextureGenerations[RequiredTextureCount] = requiredGeneration;
        RequiredTextureCount++;
    }

    internal ref VulkanFrameDependencyTicket AddDependency(
        EVulkanFrameDependencyKind kind,
        ulong resourceKey,
        ulong generation)
    {
        if (IsSealed)
            throw new InvalidOperationException("The accepted frame plan is already sealed.");
        if (DependencyCount >= _dependencies.Length)
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.Dependency,
                _dependencies.Length,
                DependencyCount + 1);

        ref VulkanFrameDependencyTicket ticket = ref _dependencies[DependencyCount++];
        ticket.Declare(kind, resourceKey, generation);
        return ref ticket;
    }

    internal ref VulkanFrameDependencyTicket AddDependencyUnique(
        EVulkanFrameDependencyKind kind,
        ulong resourceKey,
        ulong generation)
    {
        int slot = (int)(HashDependency(kind, resourceKey, generation) &
            DependencyIndexMask);
        for (int probe = 0; probe < DependencyIndexCapacity; probe++)
        {
            int storedIndex = _dependencyIndex[slot];
            if (storedIndex == 0)
            {
                ref VulkanFrameDependencyTicket added = ref AddDependency(
                    kind,
                    resourceKey,
                    generation);
                _dependencyIndex[slot] = DependencyCount;
                _dependencyIndexSlots[_dependencyIndexSlotCount++] = slot;
                return ref added;
            }

            ref VulkanFrameDependencyTicket existing =
                ref _dependencies[storedIndex - 1];
            if (existing.Kind == kind &&
                existing.ResourceKey == resourceKey &&
                existing.Generation == generation)
            {
                return ref existing;
            }

            slot = (slot + 1) & DependencyIndexMask;
        }

        throw new VulkanAcceptedFramePlanCapacityException(
            EVulkanAcceptedFrameLane.Dependency,
            DependencyCapacity,
            DependencyCount + 1);
    }

    private static ulong HashDependency(
        EVulkanFrameDependencyKind kind,
        ulong resourceKey,
        ulong generation)
    {
        ulong hash = resourceKey ^
            (generation + 0x9E3779B97F4A7C15UL +
             (resourceKey << 6) + (resourceKey >> 2));
        hash ^= (ulong)kind * 0xD6E8FEB86659FD93UL;
        hash ^= hash >> 30;
        hash *= 0xBF58476D1CE4E5B9UL;
        hash ^= hash >> 27;
        hash *= 0x94D049BB133111EBUL;
        return hash ^ (hash >> 31);
    }

    /// <summary>
    /// Builds the scalar, generation-specific readiness ledger after the exact
    /// authoring plan is frozen. Resource uses are intentionally copied as
    /// buffer tickets: the generic graph does not yet expose a stable native
    /// texture identity, while imported textures use upload tickets below.
    /// </summary>
    internal void DeclareDependencies(FramePlan logicalPlan)
    {
        ArgumentNullException.ThrowIfNull(logicalPlan);
        if (!logicalPlan.IsSealed)
            throw new VulkanPlanPreconditionException(
                "Dependency declaration requires a sealed logical plan.");

        DeclareOperationDependencies(
            logicalPlan.GetNativeStaticOperationsForRecording(),
            logicalPlan.Generation);
        DeclareOperationDependencies(
            logicalPlan.GetNativeDynamicOverlayOperationsForRecording(),
            logicalPlan.Generation);

        for (int index = 0; index < RequiredTextureUploads.Count; index++)
        {
            ref readonly VulkanTextureUploadTicket upload =
                ref RequiredTextureUploads.GetTicket(index);
            AddDependencyUnique(EVulkanFrameDependencyKind.Texture,
                unchecked((ulong)upload.Sequence),
                unchecked((ulong)upload.StreamingGeneration));
        }

        if (ShadowReadiness.RenderPlanId != 0UL)
        {
            AddDependencyUnique(EVulkanFrameDependencyKind.Shadow,
                ShadowReadiness.RenderPlanId,
                ShadowReadiness.AtlasFrameId);
        }
    }

    internal void MarkNonTextureDependenciesReady()
    {
        for (int index = 0; index < DependencyCount; index++)
        {
            ref VulkanFrameDependencyTicket ticket = ref _dependencies[index];
            if (ticket.Kind == EVulkanFrameDependencyKind.Texture)
                continue;
            AdvanceToReady(ref ticket);
        }
    }

    internal bool SynchronizeTextureDependencies()
    {
        bool progressed = false;
        for (int index = 0; index < DependencyCount; index++)
        {
            ref VulkanFrameDependencyTicket ticket = ref _dependencies[index];
            if (ticket.Kind != EVulkanFrameDependencyKind.Texture)
                continue;

            VulkanTextureUploadTicket uploadTicket = new(
                unchecked((long)ticket.ResourceKey),
                unchecked((long)ticket.Generation));
            if (!RequiredTextureUploads.TryGetState(
                    in uploadTicket,
                    out EVulkanFrameDependencyState state,
                    out ulong timelineValue,
                    out string? failureDetail))
            {
                ticket.Fail(
                    $"Accepted texture dependency sequence={uploadTicket.Sequence} " +
                    $"generation={uploadTicket.StreamingGeneration} is missing from " +
                    "the frozen required-upload manifest.");
                progressed = true;
                continue;
            }

            switch (state)
            {
                case EVulkanFrameDependencyState.CpuPrepared:
                    progressed |= ticket.MarkCpuPrepared();
                    break;
                case EVulkanFrameDependencyState.GpuSubmitted:
                    progressed |= ticket.MarkCpuPrepared();
                    progressed |= ticket.MarkGpuSubmitted(timelineValue);
                    break;
                case EVulkanFrameDependencyState.Ready:
                    progressed |= ticket.MarkCpuPrepared();
                    progressed |= ticket.MarkGpuSubmitted(timelineValue);
                    progressed |= ticket.MarkReady(timelineValue);
                    break;
                case EVulkanFrameDependencyState.TerminalFailed:
                    EVulkanFrameDependencyState beforeFailure = ticket.State;
                    ticket.Fail(failureDetail ??
                        "Required texture upload failed without a diagnostic.");
                    progressed |= beforeFailure != ticket.State;
                    break;
            }
        }

        return progressed;
    }

    private void DeclareOperationDependencies(
        FrameOperationSequence operations,
        ulong logicalPlanGeneration)
    {
        for (int operationIndex = 0;
             operationIndex < operations.Length;
             operationIndex++)
        {
            ref readonly FrameOpContext context =
                ref operations.GetContext(operationIndex);
            AddDependencyUnique(EVulkanFrameDependencyKind.Pipeline,
                unchecked((ulong)(uint)context.PipelineIdentity),
                ResolvePipelineGeneration(in context, logicalPlanGeneration));
            AddDependencyUnique(EVulkanFrameDependencyKind.Descriptor,
                ResolveDescriptorKey(in context), context.DescriptorGeneration);
            ReadOnlySpan<FrameOpResourceUse> uses =
                operations.GetResourceUses(operationIndex);
            for (int useIndex = 0; useIndex < uses.Length; useIndex++)
            {
                FrameOpResourceUse use = uses[useIndex];
                AddDependencyUnique(EVulkanFrameDependencyKind.Buffer,
                    use.ResourceId, use.Version);
            }
        }
    }

    private static ulong ResolvePipelineGeneration(
        in FrameOpContext context,
        ulong logicalPlanGeneration)
        => context.RecordingFingerprint == ulong.MaxValue
            ? logicalPlanGeneration
            : context.RecordingFingerprint;

    private static ulong ResolveDescriptorKey(in FrameOpContext context)
        => context.ContextId != 0UL
            ? context.ContextId
            : unchecked((ulong)(uint)context.SchedulingIdentity);

    private static void AdvanceToReady(ref VulkanFrameDependencyTicket ticket)
    {
        _ = ticket.MarkCpuPrepared();
        _ = ticket.MarkReady();
    }

    internal Span<VulkanFrameDependencyTicket> Dependencies
        => _dependencies.AsSpan(0, DependencyCount);

    internal void Seal(
        in RenderOutputRequest outputContract,
        FramePlan logicalPlan,
        in ResourcePlannerRuntimeState plannerState,
        in VulkanFramePlanningSnapshot frozenPlanningSnapshot)
    {
        ArgumentNullException.ThrowIfNull(logicalPlan);
        if (!logicalPlan.IsSealed)
            throw new VulkanPlanPreconditionException(
                "An accepted foreground frame requires a sealed logical plan.");
        if (_logicalPlan is not null)
            throw new InvalidOperationException(
                "The accepted frame already owns a logical plan publication.");

        ReconcileSubmissionMarkersWithSealedPlan(logicalPlan);
        logicalPlan.AcquireLease();
        _logicalPlan = logicalPlan;
        OutputContract = outputContract;
        LogicalPlanGeneration = logicalPlan.Generation;
        PlannerState = plannerState;
        FrozenPlanningSnapshot = frozenPlanningSnapshot;
        IsSealed = true;
    }

    /// <summary>
    /// Rebinds only format/output compatibility while no WSI image is owned.
    /// The accepted camera, visibility, operation, and dependency snapshot stays
    /// unchanged.
    /// </summary>
    internal void UpdateTargetCompatibility(
        in VulkanPresentNowTargetCompatibilityKey compatibility)
    {
        if (!IsSealed)
            throw new InvalidOperationException(
                "Only a sealed accepted plan may rebind target compatibility.");
        TargetCompatibility = compatibility;
    }

    internal void Reset()
    {
        SettleUnsubmittedSubmissionMarkers();
        CanonicalPublicationPins.ReleaseAll();
        if (_bindlessReceiptCount != 0)
        {
            _bindlessReceiptLeaseOwner?.ReleaseGlobalMaterialTextureReceiptLeases(
                _bindlessTextureReceipts.AsSpan(0, _bindlessReceiptCount));
            _bindlessTextureReceipts.AsSpan(0, _bindlessReceiptCount).Clear();
            _bindlessReceiptLeaseOwner = null;
            _bindlessReceiptCount = 0;
        }
        _logicalPlan?.ReleaseLease();
        _logicalPlan = null;
        _staticOperations.AsSpan(0, StaticOperationCount).Clear();
        _dynamicUiOperations.AsSpan(0, DynamicUiOperationCount).Clear();
        _textureUploadOperations.AsSpan(0, TextureUploadOperationCount).Clear();
        _requiredTextures.AsSpan(0, RequiredTextureCount).Clear();
        _requiredTextureGenerations.AsSpan(0, RequiredTextureCount).Clear();
        for (int index = 0; index < DependencyCount; index++)
            _dependencies[index].Clear();
        for (int index = 0; index < _dependencyIndexSlotCount; index++)
            _dependencyIndex[_dependencyIndexSlots[index]] = 0;
        PreparedMeshIngress.Clear();
        RequiredTextureUploads.BeginCapture();
        ShadowReadiness = default;
        ShadowReadinessResult = default;
        FrameId = 0UL;
        SceneEpoch = 0UL;
        FrameSlot = -1;
        StaticOperationCount = 0;
        DynamicUiOperationCount = 0;
        TextureUploadOperationCount = 0;
        RequiredTextureCount = 0;
        TerminalOperationCount = 0;
        MainSceneOperationCount = 0;
        ShadowOperationCount = 0;
        DependencyCount = 0;
        _dependencyIndexSlotCount = 0;
        _submissionMarkerCount = 0;
        _submissionMarkerOwnershipTransferred = false;
        OutputContract = default;
        TargetCompatibility = default;
        LogicalPlanGeneration = 0UL;
        PlannerState = default;
        FrozenPlanningSnapshot = default;
        IsSealed = false;
    }

    private static EVulkanAcceptedFrameLane ClassifyStaticOperation(FrameOp operation)
    {
        EVulkanFrameOpContextKind kind = operation.Context.ContextKind;
        if (kind == EVulkanFrameOpContextKind.Shadow)
            return EVulkanAcceptedFrameLane.Shadow;
        if (kind is EVulkanFrameOpContextKind.UiPreview or
            EVulkanFrameOpContextKind.OpenXrMirror ||
            operation.Target is null && operation.PassIndex == (int)EDefaultRenderPass.OnTopForward)
        {
            return EVulkanAcceptedFrameLane.Terminal;
        }
        return EVulkanAcceptedFrameLane.MainScene;
    }
}
