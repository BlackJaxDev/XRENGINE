using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    /// <summary>Reports whether this pipeline has an Advanced scene stage family.</summary>
    public bool UsesAdvancedScenePicking => Pipeline?.ResolveAdvancedStageFamily() is not null;

    /// <summary>Reports whether the Advanced scene stage has editor identity output enabled.</summary>
    public bool EditorPickingModuleEnabled => Pipeline?.ResolveAdvancedStageFamily() is
        { EditorHighlightModule: not null };

    private readonly object _editorPickingSync = new();
    private EditorPickingSnapshotRing? _editorPickingSnapshots;
    private EditorPickingSourceReceipt _editorPickingCandidate;
    private EditorPickingSourceReceipt _editorPickingSource;
    private XRGpuFence? _editorPickingCandidateFence;
    private bool _editorPickingCandidateAuthoringComplete;
    private bool _editorPickingCandidateExecutionRecorded;
    private ulong _editorPickingCandidateToken;
    private ulong _editorPickingActiveAuthoringToken;
    private ulong _nextEditorPickingAuthoringToken;
    private readonly EditorPickingRequestSlot[] _editorPickingRequestSlots =
        CreateEditorPickingRequestSlots();
    private readonly Action<ulong, EditorPickingEncodedPixel> _editorPickingReadbackCompleted;
    private EditorPickingRequest? _pendingEditorPickingRequest;
    private ulong _editorPickingGeneration;

    private static EditorPickingRequestSlot[] CreateEditorPickingRequestSlots()
    {
        EditorPickingRequestSlot[] slots = new EditorPickingRequestSlot[8];
        for (int index = 0; index < slots.Length; index++)
            slots[index] = new EditorPickingRequestSlot();
        return slots;
    }

    /// <summary>
    /// Copies collected mesh owners while the published package still owns its
    /// command storage. A Vulkan frame promotes this candidate after submission.
    /// </summary>
    private void PublishEditorPickingCandidate(BackendReadyFramePackage package)
    {
        if (Pipeline?.ResolveAdvancedStageFamily() is not
            { EditorHighlightModule: not null })
            return;

        lock (_editorPickingSync)
        {
            if (_editorPickingActiveAuthoringToken != 0UL)
                throw new InvalidOperationException(
                    "A nested editor identity authoring chain cannot replace its active package candidate.");
        }

        PromoteSubmittedEditorPickingCandidate();
        AbortEditorPickingCandidate();
        ReleaseStaleEditorPickingSource(package.CanonicalScenePublication.Sequence);

        if (package.State != EBackendReadyFramePackageState.Published ||
            !TryGetTexture(AdvancedRenderPipeline.EditorPickingIdentityTextureName,
                out XRTexture? identityTexture) || identityTexture is null)
            return;

        EditorPickingSnapshotRing ring = _editorPickingSnapshots ??= new();
        if (!ring.TryCapture(package, out EditorPickingSnapshotLease sidecar,
                out EditorPickingSnapshotFailure failure))
        {
            Debug.RenderingWarningEvery($"EditorPicking.Snapshot.{InstanceId}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] Editor picking source is unavailable: {0}.", failure);
            return;
        }

        AdvancedGpuScenePublicationLease native = default;
        if (package.TryGetCanonicalPublication(
                out AdvancedSharedGpuSceneDatabase database,
                out AdvancedGpuScenePublicationReference publication) &&
            publication.Snapshot is not null &&
            !database.TryAcquirePublicationLease(publication,
                EAdvancedGpuScenePublicationPinKind.Gpu, out native))
        {
            sidecar.Dispose();
            Debug.RenderingWarningEvery($"EditorPicking.NativeLease.{InstanceId}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] Editor picking cannot retain its native publication.");
            return;
        }

        EditorPickingSourceReceipt replacement = new(
            InstanceId, ResourceGeneration, identityTexture, in sidecar, in native);
        lock (_editorPickingSync)
        {
            _editorPickingCandidate = replacement;
            ++_nextEditorPickingAuthoringToken;
            if (_nextEditorPickingAuthoringToken == 0UL)
                ++_nextEditorPickingAuthoringToken;
            _editorPickingCandidateToken = _nextEditorPickingAuthoringToken;
        }
    }

    /// <summary>
    /// The largest number of scene publications that a retained editor picking source may
    /// fall behind the current frame package before the pipeline releases it.
    /// </summary>
    /// <remarks>
    /// The source keeps a GPU publication lease, and the canonical scene tables cannot trim
    /// their journals past a pinned publication. A pipeline that keeps rendering replaces its
    /// source every submitted frame. A pipeline whose frames are not submitted (for example an
    /// OpenXR eye that waits on a resource) keeps its old source, and without this bound its
    /// lease would fill the shadow-table journal until every publication is rejected.
    /// </remarks>
    private const ulong MaxEditorPickingSourcePublicationAge = 64UL;

    /// <summary>
    /// Disposes the retained editor picking source when its native publication is more than
    /// <see cref="MaxEditorPickingSourcePublicationAge"/> publications older than
    /// <paramref name="currentPublicationSequence"/>. A picking request takes its own lease, so
    /// a queued request stays valid. A later submitted frame publishes a new source.
    /// </summary>
    private void ReleaseStaleEditorPickingSource(ulong currentPublicationSequence)
    {
        if (currentPublicationSequence == 0UL)
            return;

        EditorPickingSourceReceipt stale;
        lock (_editorPickingSync)
        {
            AdvancedGpuScenePublicationLease lease = _editorPickingSource.NativeLease;
            if (!lease.IsValid ||
                lease.Reference.Sequence + MaxEditorPickingSourcePublicationAge >= currentPublicationSequence)
                return;

            stale = _editorPickingSource;
            _editorPickingSource = default;
        }
        stale.Dispose();
    }

    /// <summary>
    /// Retains the required producer fence whose ordered marker follows the
    /// editor identity seed. The marker reports native queue acceptance.
    /// </summary>
    internal bool TryAdoptEditorPickingSeedFence(XRGpuFence fence)
    {
        ArgumentNullException.ThrowIfNull(fence);
        EditorPickingSourceReceipt oldSource;
        bool adopted;
        lock (_editorPickingSync)
        {
            // The ordered seed can still submit after a later chain command
            // fails. Its image write makes the prior source untrustworthy.
            oldSource = _editorPickingSource;
            _editorPickingSource = default;
            adopted = _editorPickingCandidate.IsValid &&
                _editorPickingCandidate.ResourceGeneration == ResourceGeneration &&
                _editorPickingActiveAuthoringToken != 0UL &&
                _editorPickingActiveAuthoringToken == _editorPickingCandidateToken &&
                _editorPickingCandidateFence is null;
            if (adopted)
                _editorPickingCandidateFence = fence;
        }
        oldSource.Dispose();
        return adopted;
    }

    private void BeginEditorPickingCandidateAuthoring()
    {
        lock (_editorPickingSync)
        {
            if (!_editorPickingCandidate.IsValid)
                return;
            if (_editorPickingActiveAuthoringToken != 0UL)
                throw new InvalidOperationException(
                    "A nested editor identity authoring chain cannot begin while one is active.");
            _editorPickingActiveAuthoringToken = _editorPickingCandidateToken;
        }
    }

    private void EndEditorPickingCandidateExecution()
    {
        lock (_editorPickingSync)
        {
            _editorPickingCandidateExecutionRecorded = _editorPickingCandidate.IsValid &&
                _editorPickingCandidateFence is not null &&
                _editorPickingActiveAuthoringToken == _editorPickingCandidateToken;
            _editorPickingActiveAuthoringToken = 0UL;
        }
    }

    private void CompleteEditorPickingCandidateAuthoring()
    {
        lock (_editorPickingSync)
            _editorPickingCandidateAuthoringComplete =
                _editorPickingCandidateExecutionRecorded &&
                _editorPickingCandidate.IsValid;
    }

    private void AbortEditorPickingCandidate()
    {
        EditorPickingSourceReceipt candidate;
        XRGpuFence? fence;
        lock (_editorPickingSync)
        {
            candidate = _editorPickingCandidate;
            fence = _editorPickingCandidateFence;
            _editorPickingCandidate = default;
            _editorPickingCandidateFence = null;
            _editorPickingCandidateAuthoringComplete = false;
            _editorPickingCandidateExecutionRecorded = false;
            _editorPickingCandidateToken = 0UL;
            _editorPickingActiveAuthoringToken = 0UL;
        }
        candidate.Dispose();
        fence?.Dispose();
    }

    private void PromoteSubmittedEditorPickingCandidate()
    {
        EditorPickingSourceReceipt previous = default;
        EditorPickingSourceReceipt failed = default;
        XRGpuFence? fence = null;
        lock (_editorPickingSync)
        {
            if (!_editorPickingCandidate.IsValid ||
                !_editorPickingCandidateAuthoringComplete ||
                _editorPickingCandidateFence is null)
                return;

            EGpuFenceSubmissionStatus status =
                _editorPickingCandidateFence.SubmissionStatus;
            if (status == EGpuFenceSubmissionStatus.AwaitingSubmission)
                return;

            fence = _editorPickingCandidateFence;
            _editorPickingCandidateFence = null;
            _editorPickingCandidateAuthoringComplete = false;
            _editorPickingCandidateExecutionRecorded = false;
            if (status == EGpuFenceSubmissionStatus.Submitted)
            {
                previous = _editorPickingSource;
                _editorPickingSource = _editorPickingCandidate;
            }
            else
                failed = _editorPickingCandidate;
            _editorPickingCandidate = default;
            _editorPickingCandidateToken = 0UL;
        }
        previous.Dispose();
        failed.Dispose();
        fence?.Dispose();
    }

    /// <summary>Queues one pixel readback against a retained accepted editor source.</summary>
    public bool TryQueueEditorPicking(
        AbstractRenderer renderer,
        in AdvancedPickingQuery query,
        Action<EditorPickingResult> completed,
        out EditorPickingRequest? request,
        out string? failure)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(completed);
        request = null;
        failure = null;
        if (!RuntimeEngine.IsRenderThread)
        {
            failure = "Editor picking must be queued on the render thread.";
            return false;
        }
        if (Pipeline?.ResolveAdvancedStageFamily() is not
            { EditorHighlightModule: not null })
        {
            failure = "The active pipeline has no editor identity module.";
            return false;
        }

        PromoteSubmittedEditorPickingCandidate();

        EditorPickingRequest? oldRequest;
        lock (_editorPickingSync)
            oldRequest = _pendingEditorPickingRequest;
        oldRequest?.Dispose();

        EditorPickingRequest newRequest;
        XRTexture identityTexture;
        lock (_editorPickingSync)
        {
            EditorPickingSourceReceipt source = _editorPickingSource;
            if (!source.IsValid || source.ResourceGeneration != ResourceGeneration ||
                source.PipelineInstanceId != InstanceId ||
                !source.SidecarLease.TryRetain(out EditorPickingSnapshotLease sidecar,
                    out _))
            {
                failure = "The active editor identity attachment has no matching accepted mesh source.";
                return false;
            }

            AdvancedGpuScenePublicationLease native = default;
            if (source.NativeLease.IsValid &&
                (source.NativeLease.Database is not { } database ||
                 !database.TryAcquirePublicationLease(source.NativeLease.Reference,
                     EAdvancedGpuScenePublicationPinKind.Gpu, out native)))
            {
                sidecar.Dispose();
                failure = "The accepted native editor source could not retain its publication.";
                return false;
            }

            int slotIndex = -1;
            for (int index = 0; index < _editorPickingRequestSlots.Length; index++)
            {
                if (_editorPickingRequestSlots[index].State != EEditorPickingRequestState.Free)
                    continue;
                slotIndex = index;
                break;
            }
            if (slotIndex < 0)
            {
                sidecar.Dispose();
                native.Dispose();
                failure = "The bounded editor picking readback pool is full.";
                return false;
            }

            ++_editorPickingGeneration;
            if (_editorPickingGeneration == 0UL)
                ++_editorPickingGeneration;
            EditorPickingRequestSlot slot = _editorPickingRequestSlots[slotIndex];
            slot.State = EEditorPickingRequestState.Pending;
            slot.Generation = _editorPickingGeneration;
            slot.Query = query;
            slot.ResourceGeneration = source.ResourceGeneration;
            slot.PackageIdentity = source.PackageIdentity;
            slot.PackageGeneration = source.PackageGeneration;
            slot.IdentityTexture = source.IdentityTexture;
            slot.SidecarLease = sidecar;
            slot.NativeLease = native;
            slot.Completed = completed;
            newRequest = new EditorPickingRequest(this, slotIndex,
                _editorPickingGeneration);
            identityTexture = source.IdentityTexture!;
            _pendingEditorPickingRequest = newRequest;
            request = newRequest;
        }

        if (renderer.TryQueueEditorPickingReadback(identityTexture, in query,
                newRequest.Generation, _editorPickingReadbackCompleted, out failure))
            return true;

        newRequest.Dispose();
        request = null;
        failure ??= "The renderer rejected the asynchronous editor identity readback.";
        return false;
    }

    internal bool IsEditorPickingRequestPending(int slotIndex, ulong generation)
    {
        lock (_editorPickingSync)
            return (uint)slotIndex < (uint)_editorPickingRequestSlots.Length &&
                _editorPickingRequestSlots[slotIndex].State != EEditorPickingRequestState.Free &&
                _editorPickingRequestSlots[slotIndex].Generation == generation;
    }

    internal bool IsEditorPickingRequestActive(int slotIndex, ulong generation)
    {
        lock (_editorPickingSync)
            return (uint)slotIndex < (uint)_editorPickingRequestSlots.Length &&
                _editorPickingRequestSlots[slotIndex].State != EEditorPickingRequestState.Free &&
                _editorPickingRequestSlots[slotIndex].Generation == generation;
    }

    internal void CancelEditorPickingRequest(int slotIndex, ulong generation)
    {
        EditorPickingSnapshotLease sidecar;
        AdvancedGpuScenePublicationLease native;
        lock (_editorPickingSync)
        {
            if ((uint)slotIndex >= (uint)_editorPickingRequestSlots.Length)
                return;
            EditorPickingRequestSlot slot = _editorPickingRequestSlots[slotIndex];
            if (slot.Generation != generation)
                return;
            if (slot.State == EEditorPickingRequestState.Delivering)
            {
                slot.SuppressDelivery = true;
                return;
            }
            if (slot.State != EEditorPickingRequestState.Pending)
                return;
            sidecar = slot.SidecarLease;
            native = slot.NativeLease;
            slot.Clear();
            if (_pendingEditorPickingRequest is { } pending &&
                pending.Generation == generation)
                _pendingEditorPickingRequest = null;
        }
        sidecar.Dispose();
        native.Dispose();
    }

    private void CompleteEditorPicking(ulong token, EditorPickingEncodedPixel pixel)
    {
        EditorPickingRequestSlot? slot = null;
        EditorPickingSnapshotLease sidecar = default;
        AdvancedGpuScenePublicationLease nativeLease = default;
        AdvancedPickingQuery query = default;
        BackendReadyFramePackageIdentity packageIdentity = default;
        long packageGeneration = 0L;
        lock (_editorPickingSync)
        {
            for (int index = 0; index < _editorPickingRequestSlots.Length; index++)
            {
                EditorPickingRequestSlot candidate = _editorPickingRequestSlots[index];
                if (candidate.Generation != token ||
                    candidate.State != EEditorPickingRequestState.Pending)
                    continue;
                candidate.State = EEditorPickingRequestState.Delivering;
                slot = candidate;
                sidecar = candidate.SidecarLease;
                nativeLease = candidate.NativeLease;
                query = candidate.Query;
                packageIdentity = candidate.PackageIdentity;
                packageGeneration = candidate.PackageGeneration;
                if (_pendingEditorPickingRequest is { } pending &&
                    pending.Generation == token)
                    _pendingEditorPickingRequest = null;
                break;
            }
        }
        if (slot is null)
            return;

        EditorPickingResult result = EditorPickingResult.Miss(token,
            packageIdentity, packageGeneration, query.ViewIndex);
        try
        {
            if (pixel.TryDecode(out uint key, out EditorPickingSourceKind kind))
            {
                if (kind == EditorPickingSourceKind.AuthoredCommand)
                    _editorPickingSnapshots?.TryResolve(in sidecar, in pixel,
                        token, query.ViewIndex, out result);
                else if (kind == EditorPickingSourceKind.NativeDraw &&
                    nativeLease.Reference.Snapshot is { } native)
                    EditorPickingNativeResolver.TryResolve(in sidecar, native, key,
                        token, query.ViewIndex, out result);
            }
        }
        finally
        {
            Action<EditorPickingResult>? completed;
            lock (_editorPickingSync)
                completed = slot.SuppressDelivery ||
                    slot.ResourceGeneration != ResourceGeneration
                    ? null : slot.Completed;
            sidecar.Dispose();
            nativeLease.Dispose();
            try
            {
                completed?.Invoke(result);
            }
            finally
            {
                lock (_editorPickingSync)
                    slot.Clear();
            }
        }
    }

    /// <summary>
    /// Releases an idle editor source before its native lease pins old scene
    /// table journals. A later accepted frame supplies a new source.
    /// </summary>
    internal void ReleaseLaggingEditorPickingRetention()
    {
        EditorPickingSourceReceipt source;
        EditorPickingSourceReceipt candidate;
        lock (_editorPickingSync)
        {
            source = _editorPickingSource;
            candidate = _editorPickingCandidate;
        }
        AdvancedGpuScenePublicationLease sourceNative = source.NativeLease;
        AdvancedGpuScenePublicationLease candidateNative = candidate.NativeLease;
        bool dropSource = IsLaggingPickingLease(in sourceNative);
        bool dropCandidate = IsLaggingPickingLease(in candidateNative);
        if (!dropSource && !dropCandidate)
            return;

        EditorPickingSourceReceipt droppedSource = default;
        EditorPickingSourceReceipt droppedCandidate = default;
        XRGpuFence? droppedFence = null;
        EditorPickingSnapshotLease sourceSidecar = source.SidecarLease;
        EditorPickingSnapshotLease candidateSidecar = candidate.SidecarLease;
        lock (_editorPickingSync)
        {
            EditorPickingSnapshotLease currentSourceSidecar =
                _editorPickingSource.SidecarLease;
            EditorPickingSnapshotLease currentCandidateSidecar =
                _editorPickingCandidate.SidecarLease;
            if (dropSource &&
                currentSourceSidecar.IsSameLease(in sourceSidecar))
            {
                droppedSource = _editorPickingSource;
                _editorPickingSource = default;
            }
            if (dropCandidate && _editorPickingActiveAuthoringToken == 0UL &&
                currentCandidateSidecar.IsSameLease(in candidateSidecar))
            {
                droppedCandidate = _editorPickingCandidate;
                droppedFence = _editorPickingCandidateFence;
                _editorPickingCandidate = default;
                _editorPickingCandidateFence = null;
                _editorPickingCandidateAuthoringComplete = false;
                _editorPickingCandidateExecutionRecorded = false;
                _editorPickingCandidateToken = 0UL;
                _editorPickingActiveAuthoringToken = 0UL;
            }
        }
        droppedSource.Dispose();
        droppedCandidate.Dispose();
        droppedFence?.Dispose();
    }

    private void CancelPendingEditorPicking()
    {
        EditorPickingRequest? pending;
        EditorPickingSourceReceipt source;
        EditorPickingSourceReceipt candidate;
        XRGpuFence? fence;
        lock (_editorPickingSync)
        {
            pending = _pendingEditorPickingRequest;
            for (int index = 0; index < _editorPickingRequestSlots.Length; index++)
            {
                if (_editorPickingRequestSlots[index].State ==
                    EEditorPickingRequestState.Delivering)
                    _editorPickingRequestSlots[index].SuppressDelivery = true;
            }
            source = _editorPickingSource;
            candidate = _editorPickingCandidate;
            fence = _editorPickingCandidateFence;
            _pendingEditorPickingRequest = null;
            _editorPickingSource = default;
            _editorPickingCandidate = default;
            _editorPickingCandidateFence = null;
            _editorPickingCandidateAuthoringComplete = false;
            _editorPickingCandidateExecutionRecorded = false;
            _editorPickingCandidateToken = 0UL;
            _editorPickingActiveAuthoringToken = 0UL;
        }
        pending?.Dispose();
        source.Dispose();
        candidate.Dispose();
        fence?.Dispose();
    }
}
