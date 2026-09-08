using System.Threading;
using System.Runtime.InteropServices;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns queued frame operations and the reusable capture workspace for each
/// recording thread. The renderer facade supplies policy and operation
/// construction, while this owner contains all mutable queue/capture state.
/// </summary>
internal sealed class VulkanFrameOperationQueue : IDisposable
{
    private const int AdvancedVisibilityLeaseCapacity = 16;
    internal const int FrameViewHistoryCapacity = 512;
    internal const int OutputCompletionCapacity = 512;

    private readonly ThreadLocal<ThreadWorkspace> _threadWorkspace =
        new(static () => new ThreadWorkspace(), trackAllValues: false);
    private readonly VulkanAdvancedVisibilityInputLease[]
        _advancedVisibilityInputLeases = CreateAdvancedVisibilityInputLeases();
    private readonly FrameViewHistoryEntry[] _frameViewHistory =
        new FrameViewHistoryEntry[FrameViewHistoryCapacity];
    private readonly uint[] _frameViewHistoryGenerations = new uint[FrameViewHistoryCapacity];
    private readonly RenderFrameViewHistoryBackendReservation[] _drainedFrameViewHistory =
        new RenderFrameViewHistoryBackendReservation[FrameViewHistoryCapacity];
    private readonly OutputCompletionEntry[] _outputCompletions =
        new OutputCompletionEntry[OutputCompletionCapacity];
    private readonly uint[] _outputCompletionGenerations =
        new uint[OutputCompletionCapacity];
    private readonly RenderOutputCompletionBackendReservation[] _drainedOutputCompletions =
        new RenderOutputCompletionBackendReservation[OutputCompletionCapacity];
    private ulong _nextOutputCompletionReceiptId;

    public Lock SyncRoot { get; } = new();
    public List<FrameOp> Pending { get; } = [];
    public FrameOp[] DrainedFrameOpsBuffer { get; set; } = [];
    public FrameOp[] DrainedTextureUploadFrameOpsBuffer { get; set; } = [];
    internal VulkanFrameOpDiagnosticsState Diagnostics { get; } = new();
    internal int DrainedFrameViewHistoryCount { get; private set; }
    internal ReadOnlySpan<RenderFrameViewHistoryBackendReservation> DrainedFrameViewHistory
        => _drainedFrameViewHistory.AsSpan(0, DrainedFrameViewHistoryCount);
    internal int DrainedOutputCompletionCount { get; private set; }
    internal ReadOnlySpan<RenderOutputCompletionBackendReservation> DrainedOutputCompletions
        => _drainedOutputCompletions.AsSpan(0, DrainedOutputCompletionCount);

    internal bool TryReserveOutputCompletion(
        in RenderOutputRequest output,
        XRFrameBuffer targetFrameBuffer,
        VulkanTimelineGpuFence fence,
        out RenderOutputCompletionBackendReservation reservation)
    {
        using (SyncRoot.EnterScope())
        {
            for (int index = 0; index < _outputCompletions.Length; index++)
            {
                ref OutputCompletionEntry entry = ref _outputCompletions[index];
                if (entry.State != EOutputCompletionState.Empty)
                    continue;
                uint generation = NextNonZero(_outputCompletionGenerations[index]);
                _outputCompletionGenerations[index] = generation;
                ulong receiptId = unchecked(++_nextOutputCompletionReceiptId);
                if (receiptId == 0UL)
                    receiptId = unchecked(++_nextOutputCompletionReceiptId);
                entry.Reservation = new(
                    receiptId,
                    output,
                    targetFrameBuffer,
                    fence,
                    index,
                    generation);
                entry.State = EOutputCompletionState.Reserved;
                reservation = entry.Reservation;
                return true;
            }
        }
        reservation = default;
        return false;
    }

    internal void CompleteOutputCompletionAuthoring(
        in RenderOutputCompletionBackendReservation reservation,
        bool succeeded)
    {
        using (SyncRoot.EnterScope())
        {
            if (!TryGetOutputCompletionEntry(in reservation, out int index))
                return;
            ref OutputCompletionEntry entry = ref _outputCompletions[index];
            if (!succeeded)
            {
                FailOutputCompletionFence(entry.Reservation.Fence);
                entry = default;
                return;
            }
            entry.State = EOutputCompletionState.Ready;
        }
    }

    internal void ReleaseDrainedOutputCompletionOwnership()
    {
        _drainedOutputCompletions.AsSpan(0, DrainedOutputCompletionCount).Clear();
        DrainedOutputCompletionCount = 0;
    }

    internal void DiscardDrainedOutputCompletionOwnership()
    {
        for (int index = 0; index < DrainedOutputCompletionCount; index++)
            FailOutputCompletionFence(_drainedOutputCompletions[index].Fence);
        ReleaseDrainedOutputCompletionOwnership();
    }

    /// <summary>Transfers the current drained aliases to an accepted plan.</summary>
    internal void ReleaseDrainedFrameViewHistoryOwnership()
    {
        _drainedFrameViewHistory.AsSpan(0, DrainedFrameViewHistoryCount).Clear();
        DrainedFrameViewHistoryCount = 0;
    }

    /// <summary>
    /// Discards only the history cohort removed by the last atomic frame drain.
    /// Reservations and operations published after that drain remain queued for
    /// the next accepted frame.
    /// </summary>
    internal void DiscardDrainedFrameViewHistoryOwnership()
    {
        for (int index = 0; index < DrainedFrameViewHistoryCount; index++)
            _drainedFrameViewHistory[index].Candidate.Discard();
        ReleaseDrainedFrameViewHistoryOwnership();
    }

    /// <summary>
    /// Clears reusable drain-buffer aliases after an accepted plan copies and
    /// assumes ownership of their operations.
    /// </summary>
    internal void ReleaseDrainedOperationAliases()
    {
        Array.Clear(DrainedFrameOpsBuffer);
        Array.Clear(DrainedTextureUploadFrameOpsBuffer);
    }

    /// <summary>
    /// Settles a drained operation cohort when accepted-plan capture rejects it
    /// before ownership transfers. Concurrently published Pending work is not
    /// inspected or changed.
    /// </summary>
    internal void DiscardDrainedOperationOwnership()
    {
        FailPendingSubmissionMarkers(DrainedFrameOpsBuffer);
        FailPendingSubmissionMarkers(DrainedTextureUploadFrameOpsBuffer);
        VulkanAdvancedVisibilityInputLease.ReleaseOperations(
            DrainedFrameOpsBuffer);
        VulkanAdvancedVisibilityInputLease.ReleaseOperations(
            DrainedTextureUploadFrameOpsBuffer);
        ReleaseDrainedOperationAliases();
    }

    internal bool TryReserveFrameViewHistory(
        in RenderFrameViewHistoryCandidateToken candidate,
        in RenderOutputRequest output,
        XRFrameBuffer? targetFrameBuffer,
        out RenderFrameViewHistoryBackendReservation reservation)
    {
        using (SyncRoot.EnterScope())
        {
            for (int index = 0; index < _frameViewHistory.Length; index++)
            {
                ref FrameViewHistoryEntry entry = ref _frameViewHistory[index];
                if (entry.State != EFrameViewHistoryState.Empty)
                    continue;
                uint generation = NextNonZero(_frameViewHistoryGenerations[index]);
                _frameViewHistoryGenerations[index] = generation;
                entry.Reservation = new(
                    candidate,
                    output,
                    targetFrameBuffer,
                    index,
                    generation);
                entry.State = EFrameViewHistoryState.Reserved;
                reservation = entry.Reservation;
                return true;
            }
        }
        reservation = default;
        return false;
    }

    internal void CompleteFrameViewHistoryAuthoring(in RenderFrameViewHistoryBackendReservation reservation, bool succeeded)
    {
        using (SyncRoot.EnterScope())
        {
            if (!TryGetFrameViewHistoryEntry(in reservation, out int index))
                return;
            ref FrameViewHistoryEntry entry = ref _frameViewHistory[index];
            if (!succeeded)
            {
                entry.Reservation.Candidate.Discard();
                entry = default;
                return;
            }
            entry.State = EFrameViewHistoryState.Ready;
        }
    }

    /// <summary>
    /// Gets the reusable workspace scoped to the calling recording thread.
    /// The first access on a thread allocates the workspace; warmed steady-state
    /// access and capture reuse are allocation-free.
    /// </summary>
    public ThreadWorkspace CurrentThread
        => _threadWorkspace.Value
            ?? throw new InvalidOperationException(
                "The Vulkan frame-operation queue has been disposed.");

    public void ReleaseCurrentThread()
        => CurrentThread.Reset();

    /// <summary>
    /// Retains one immutable visibility snapshot reference before the authored
    /// operation crosses into a deferred queue or capture cohort.
    /// </summary>
    internal bool TryAcquireAdvancedVisibilityInput(
        in VulkanAdvancedVisibilityStageRequest request,
        out VulkanAdvancedVisibilityInputLease lease,
        out string failureReason)
    {
        using (SyncRoot.EnterScope())
        {
            for (int index = 0;
                 index < _advancedVisibilityInputLeases.Length;
                 ++index)
            {
                VulkanAdvancedVisibilityInputLease candidate =
                    _advancedVisibilityInputLeases[index];
                if (candidate.MatchesRequest(in request) &&
                    candidate.TryRetain())
                {
                    lease = candidate;
                    failureReason = "Ready";
                    return true;
                }
            }

            for (int index = 0;
                 index < _advancedVisibilityInputLeases.Length;
                 ++index)
            {
                VulkanAdvancedVisibilityInputLease candidate =
                    _advancedVisibilityInputLeases[index];
                if (!candidate.IsAvailable)
                    continue;

                // Every free slot captures the same publication. A source failure
                // cannot be repaired by trying another slot, and is distinct from
                // exhausting the bounded arena.
                if (!candidate.TryCapture(in request, out failureReason))
                {
                    lease = null!;
                    return false;
                }

                lease = candidate;
                return true;
            }
        }

        lease = null!;
        failureReason =
            $"The bounded advanced visibility authoring lease arena exhausted its " +
            $"{AdvancedVisibilityLeaseCapacity} concurrent families.";
        return false;
    }

    /// <summary>
    /// Publishes one fully prepared operation into the active capture or the shared
    /// pending stream. Producer-side validation and resource lowering must finish
    /// before this scheduling boundary.
    /// </summary>
    internal void EnqueuePrepared(FrameOp operation)
    {
        VulkanShadowAtlasDiagnostics.RecordEnqueuedOperation(operation);
        FrameOpCapture? capture = CurrentThread.Capture;
        if (capture is not null)
        {
            if (capture.ExcludeTextureUploads && operation is TextureUploadFrameOp)
            {
                using (SyncRoot.EnterScope())
                    Pending.Add(operation);
            }
            else
            {
                capture.Add(operation);
            }

            return;
        }

        using (SyncRoot.EnterScope())
            Pending.Add(operation);
    }

    internal bool TryBeginOrderedBatch()
    {
        if (CurrentThread.OrderedComputeBatchCapture is not null)
            return false;

        FrameOpCapture capture = CurrentThread.OrderedComputeBatchCaptureScratch ??= new FrameOpCapture();
        capture.Begin(CurrentThread.Capture, excludeTextureUploads: false);
        CurrentThread.OrderedComputeBatchCapture = capture;
        CurrentThread.Capture = capture;
        return true;
    }

    internal void CommitOrderedBatch()
    {
        FrameOpCapture capture = EndOrderedBatch();
        FrameOpCapture? previous = capture.Previous;
        if (previous is not null)
        {
            for (int index = 0; index < capture.Count; index++)
                previous.Add(capture.Buffer[index]);
            return;
        }

        using (SyncRoot.EnterScope())
            for (int index = 0; index < capture.Count; index++)
                Pending.Add(capture.Buffer[index]);
    }

    internal void RollbackOrderedBatch()
    {
        FrameOpCapture capture = EndOrderedBatch();
        for (int index = 0; index < capture.Count; index++)
        {
            capture.Buffer[index].ReleaseAuthoringSnapshot();
            if (capture.Buffer[index] is SubmissionMarkerOp marker)
                marker.Fence.Fail();
            if (capture.Buffer[index] is AdvancedVisibilityOp visibility)
                visibility.ReleaseInputLease();
        }
    }

    private FrameOpCapture EndOrderedBatch()
    {
        FrameOpCapture capture = CurrentThread.OrderedComputeBatchCapture
            ?? throw new InvalidOperationException("No ordered compute batch is active on this thread.");
        CurrentThread.Capture = capture.Previous;
        CurrentThread.OrderedComputeBatchCapture = null;
        return capture;
    }

    internal bool TryGetLastForTarget(XRFrameBuffer target, out FrameOp operation)
    {
        FrameOpCapture? capture = CurrentThread.Capture;
        if (capture is not null)
        {
            for (int index = capture.Count - 1; index >= 0; index--)
            {
                FrameOp candidate = capture.Buffer[index];
                if (Targets(candidate, target))
                {
                    operation = candidate;
                    return true;
                }
            }
        }

        using (SyncRoot.EnterScope())
        {
            for (int index = Pending.Count - 1; index >= 0; index--)
            {
                FrameOp candidate = Pending[index];
                if (Targets(candidate, target))
                {
                    operation = candidate;
                    return true;
                }
            }
        }

        operation = null!;
        return false;
    }

    private static bool Targets(FrameOp operation, XRFrameBuffer target)
        => operation is not PublishFramebufferForSamplingOp &&
           ReferenceEquals(operation.Target, target);

    internal bool EnqueuePreparedQuery(
        VkRenderQuery query,
        in RenderQueryDescriptor descriptor,
        ERenderQueryOperation operation,
        int passIndex,
        XRFrameBuffer? target,
        in FrameOpContext context,
        Silk.NET.Vulkan.PipelineStageFlags2 timestampStage = Silk.NET.Vulkan.PipelineStageFlags2.AllCommandsBit,
        uint pointIndex = 0u)
    {
        if (descriptor.Kind == ERenderQueryKind.Occlusion &&
            RenderDiagnosticsFlags.VkSkipOcclusionQueryOps &&
            (operation == ERenderQueryOperation.Begin || CurrentThread.RenderQueryBracketDepth == 0))
        {
            Debug.VulkanWarningEvery(
                "Vulkan.OcclusionQueryOpsSkipped",
                TimeSpan.FromSeconds(5),
                "[Vulkan] Skipping occlusion QueryOp {0} for command-chain ceiling diagnostics ({1}=1). Query results remain stale/conservative.",
                operation,
                XREngineEnvironmentVariables.VkSkipOcclusionQueryOps);
            return false;
        }

        EnqueuePrepared(new QueryOp(
            passIndex,
            target,
            query,
            descriptor,
            operation,
            context,
            timestampStage,
            pointIndex));
        if (operation == ERenderQueryOperation.Begin)
            CurrentThread.RenderQueryBracketDepth++;
        else if (operation == ERenderQueryOperation.End && CurrentThread.RenderQueryBracketDepth > 0)
            CurrentThread.RenderQueryBracketDepth--;
        return true;
    }

    internal FrameOp[] Capture(Action emitOperations, bool excludeTextureUploads)
    {
        FrameOpCapture? previous = CurrentThread.Capture;
        FrameOpCapture capture = RentCapture(previous, excludeTextureUploads);
        CurrentThread.Capture = capture;
        try
        {
            emitOperations();
        }
        catch
        {
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                capture.Buffer.AsSpan(0, capture.Count));
            throw;
        }
        finally
        {
            CurrentThread.Capture = previous;
        }

        return CopyCapture(capture);
    }

    internal FrameOp[] Capture(
        IOpenXrEyeFrameOpEmitter emitter,
        in OpenXrEyeFrameOpEmission emission,
        bool excludeTextureUploads)
    {
        FrameOpCapture? previous = CurrentThread.Capture;
        FrameOpCapture capture = RentCapture(previous, excludeTextureUploads);
        CurrentThread.Capture = capture;
        try
        {
            emitter.Emit(emission);
        }
        catch
        {
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                capture.Buffer.AsSpan(0, capture.Count));
            throw;
        }
        finally
        {
            CurrentThread.Capture = previous;
        }

        return CopyCapture(capture);
    }

    private FrameOpCapture RentCapture(FrameOpCapture? previous, bool excludeTextureUploads)
    {
        FrameOpCapture capture = previous is null
            ? CurrentThread.CaptureScratch ??= new FrameOpCapture()
            : new FrameOpCapture();
        capture.Begin(previous, excludeTextureUploads);
        return capture;
    }

    private FrameOp[] CopyCapture(FrameOpCapture capture)
    {
        int operationCount = capture.Count;
        if (operationCount == 0)
            return [];

        Dictionary<int, FrameOp[]> buffers = CurrentThread.CaptureBuffersByCount;
        if (!buffers.TryGetValue(operationCount, out FrameOp[]? result))
        {
            result = new FrameOp[operationCount];
            buffers.Add(operationCount, result);
        }
        else
        {
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(result);
        }

        Array.Copy(capture.Buffer, result, operationCount);
        return result;
    }

    internal FrameOp[] DrainPending()
    {
        using (SyncRoot.EnterScope())
        {
            if (Pending.Count == 0)
                return [];

            int operationCount = 0;
            for (int index = 0; index < Pending.Count; index++)
                if (Pending[index].ContextReference.OutputCompletionReceiptId == 0UL)
                    operationCount++;
            if (operationCount == 0)
                return [];
            if (DrainedFrameOpsBuffer.Length != operationCount)
                DrainedFrameOpsBuffer = new FrameOp[operationCount];

            int drainedIndex = 0;
            int retainedIndex = 0;
            int pendingCount = Pending.Count;
            for (int index = 0; index < pendingCount; index++)
            {
                FrameOp operation = Pending[index];
                if (operation.ContextReference.OutputCompletionReceiptId == 0UL)
                    DrainedFrameOpsBuffer[drainedIndex++] = operation;
                else
                    Pending[retainedIndex++] = operation;
            }
            if (retainedIndex < Pending.Count)
                Pending.RemoveRange(retainedIndex, Pending.Count - retainedIndex);
            return DrainedFrameOpsBuffer;
        }
    }

    /// <summary>Atomically drains scene and upload operations for one frame-plan preparation.</summary>
    internal FrameOp[] DrainForPrimary(
        out FrameOp[] textureUploadOperations,
        bool drainFrameViewHistory = true,
        ReadOnlySpan<RenderOutputCompletionBackendReservation>
            acceptedOutputCompletions = default)
    {
        using (SyncRoot.EnterScope())
        {
            int operationCount = Pending.Count;
            if (operationCount == 0)
            {
                if (drainFrameViewHistory)
                {
                    DrainReadyFrameViewHistory();
                    DrainReadyOutputCompletions();
                }
                textureUploadOperations = [];
                return [];
            }

            int uploadCount = 0;
            int sceneCount = 0;
            for (int index = 0; index < operationCount; index++)
            {
                FrameOp operation = Pending[index];
                ulong receiptId = operation.ContextReference.OutputCompletionReceiptId;
                if (receiptId != 0UL &&
                    !IsOutputCompletionDrainable(
                        receiptId,
                        drainFrameViewHistory,
                        acceptedOutputCompletions))
                    continue;
                if (operation is TextureUploadFrameOp)
                    uploadCount++;
                else
                    sceneCount++;
            }

            if (DrainedFrameOpsBuffer.Length != sceneCount)
                DrainedFrameOpsBuffer = new FrameOp[sceneCount];
            if (DrainedTextureUploadFrameOpsBuffer.Length != uploadCount)
                DrainedTextureUploadFrameOpsBuffer = new FrameOp[uploadCount];

            int sceneIndex = 0;
            int uploadIndex = 0;
            int retainedIndex = 0;
            for (int index = 0; index < operationCount; index++)
            {
                FrameOp operation = Pending[index];
                ulong receiptId = operation.ContextReference.OutputCompletionReceiptId;
                if (receiptId != 0UL &&
                    !IsOutputCompletionDrainable(
                        receiptId,
                        drainFrameViewHistory,
                        acceptedOutputCompletions))
                {
                    Pending[retainedIndex++] = operation;
                    continue;
                }
                if (operation is TextureUploadFrameOp)
                    DrainedTextureUploadFrameOpsBuffer[uploadIndex++] = operation;
                else
                    DrainedFrameOpsBuffer[sceneIndex++] = operation;
            }

            if (retainedIndex < Pending.Count)
                Pending.RemoveRange(retainedIndex, Pending.Count - retainedIndex);
            if (drainFrameViewHistory)
            {
                DrainReadyFrameViewHistory();
                DrainReadyOutputCompletions();
            }
            textureUploadOperations = DrainedTextureUploadFrameOpsBuffer;
            return DrainedFrameOpsBuffer;
        }
    }

    private bool IsOutputCompletionDrainable(
        ulong receiptId,
        bool drainReadyOutputCompletions,
        ReadOnlySpan<RenderOutputCompletionBackendReservation>
            acceptedOutputCompletions)
    {
        if (drainReadyOutputCompletions)
        {
            for (int index = 0; index < _outputCompletions.Length; index++)
            {
                ref readonly OutputCompletionEntry entry =
                    ref _outputCompletions[index];
                if (entry.State == EOutputCompletionState.Ready &&
                    entry.Reservation.ReceiptId == receiptId)
                    return true;
            }
        }

        for (int index = 0; index < acceptedOutputCompletions.Length; index++)
            if (acceptedOutputCompletions[index].ReceiptId == receiptId)
                return true;
        return false;
    }

    /// <summary>
    /// Validates the source-contiguous required cohort before its terminal marker
    /// is appended. A uniform recording context keeps stable sorting from moving
    /// the marker ahead of one of the members it certifies.
    /// </summary>
    internal bool TryGetRequiredOrderedBatchOperationCount(
        int markerPassIndex,
        in FrameOpContext markerContext,
        out int requiredOperationCount,
        out string failureReason)
    {
        FrameOpCapture capture = CurrentThread.OrderedComputeBatchCapture
            ?? throw new InvalidOperationException(
                "No ordered compute batch is active on this thread.");
        requiredOperationCount = capture.Count;
        if (requiredOperationCount == 0)
        {
            failureReason =
                "The required Vulkan GPU producer completed without publishing any frame operations.";
            return false;
        }

        for (int index = 0; index < requiredOperationCount; index++)
        {
            FrameOp operation = capture.Buffer[index];
            ref readonly FrameOpContext operationContext =
                ref operation.ContextReference;
            if (operation is SubmissionMarkerOp)
            {
                failureReason =
                    "A required Vulkan GPU producer cannot contain a nested submission marker.";
                return false;
            }
            if (operation is TextureUploadFrameOp)
            {
                failureReason =
                    "A required Vulkan GPU producer cannot certify a texture upload that is submitted through a separate command stream.";
                return false;
            }
            if (operation.PassIndex != markerPassIndex ||
                !FrameOpContextCompatibility.AreRecordingCompatible(
                    in operationContext,
                    in markerContext))
            {
                failureReason =
                    $"Required Vulkan GPU producer operation {index} does not share the terminal marker's frozen pass and recording context.";
                return false;
            }
        }

        failureReason = string.Empty;
        return true;
    }

    private void DrainReadyFrameViewHistory()
    {
        // A primary path that did not claim the previous batch cannot leave
        // ledger receipts alive for an unrelated successor.
        for (int index = 0; index < DrainedFrameViewHistoryCount; index++)
            _drainedFrameViewHistory[index].Candidate.Discard();
        DrainedFrameViewHistoryCount = 0;
        for (int index = 0; index < _frameViewHistory.Length; index++)
        {
            ref FrameViewHistoryEntry entry = ref _frameViewHistory[index];
            if (entry.State != EFrameViewHistoryState.Ready)
                continue;
            _drainedFrameViewHistory[DrainedFrameViewHistoryCount++] = entry.Reservation;
            entry = default;
        }
    }

    private void DrainReadyOutputCompletions()
    {
        for (int index = 0; index < DrainedOutputCompletionCount; index++)
            FailOutputCompletionFence(_drainedOutputCompletions[index].Fence);
        DrainedOutputCompletionCount = 0;
        for (int index = 0; index < _outputCompletions.Length; index++)
        {
            ref OutputCompletionEntry entry = ref _outputCompletions[index];
            if (entry.State != EOutputCompletionState.Ready)
                continue;
            _drainedOutputCompletions[DrainedOutputCompletionCount++] =
                entry.Reservation;
            entry = default;
        }
    }

    /// <summary>Drains only uploads while retaining scene operations in queue order.</summary>
    internal FrameOp[] DrainTextureUploads()
    {
        using (SyncRoot.EnterScope())
        {
            int operationCount = Pending.Count;
            int uploadCount = 0;
            for (int index = 0; index < operationCount; index++)
                if (Pending[index] is TextureUploadFrameOp &&
                    Pending[index].ContextReference.OutputCompletionReceiptId == 0UL)
                    uploadCount++;

            if (uploadCount == 0)
                return [];
            if (DrainedTextureUploadFrameOpsBuffer.Length != uploadCount)
                DrainedTextureUploadFrameOpsBuffer = new FrameOp[uploadCount];

            int retainedIndex = 0;
            int uploadIndex = 0;
            for (int index = 0; index < operationCount; index++)
            {
                FrameOp operation = Pending[index];
                if (operation is TextureUploadFrameOp &&
                    operation.ContextReference.OutputCompletionReceiptId == 0UL)
                    DrainedTextureUploadFrameOpsBuffer[uploadIndex++] = operation;
                else
                    Pending[retainedIndex++] = operation;
            }

            if (retainedIndex < Pending.Count)
                Pending.RemoveRange(retainedIndex, Pending.Count - retainedIndex);
            return DrainedTextureUploadFrameOpsBuffer;
        }
    }

    /// <summary>Drains scene operations while retaining uploads in queue order.</summary>
    internal FrameOp[] DrainExcludingTextureUploads()
    {
        using (SyncRoot.EnterScope())
        {
            int operationCount = Pending.Count;
            int uploadCount = 0;
            for (int index = 0; index < operationCount; index++)
                if (Pending[index] is TextureUploadFrameOp ||
                    Pending[index].ContextReference.OutputCompletionReceiptId != 0UL)
                    uploadCount++;

            int drainedCount = operationCount - uploadCount;
            if (drainedCount == 0)
                return [];
            if (DrainedFrameOpsBuffer.Length != drainedCount)
                DrainedFrameOpsBuffer = new FrameOp[drainedCount];

            int drainedIndex = 0;
            int retainedIndex = 0;
            for (int index = 0; index < operationCount; index++)
            {
                FrameOp operation = Pending[index];
                if (operation is TextureUploadFrameOp ||
                    operation.ContextReference.OutputCompletionReceiptId != 0UL)
                    Pending[retainedIndex++] = operation;
                else
                    DrainedFrameOpsBuffer[drainedIndex++] = operation;
            }

            if (retainedIndex < Pending.Count)
                Pending.RemoveRange(retainedIndex, Pending.Count - retainedIndex);
            return DrainedFrameOpsBuffer;
        }
    }

    /// <summary>
    /// Atomically abandons all unsubmitted queue work during renderer teardown
    /// or an explicit terminal reset.
    /// </summary>
    internal void Reset()
    {
        using (SyncRoot.EnterScope())
        {
            // Only Pending remains queue-owned. Drained buffers are reusable
            // scratch aliases whose operations may already belong to an accepted
            // frame plan or asynchronous submission tracker.
            FailPendingSubmissionMarkers(CollectionsMarshal.AsSpan(Pending));
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                CollectionsMarshal.AsSpan(Pending));
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                DrainedFrameOpsBuffer);
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                DrainedTextureUploadFrameOpsBuffer);
            Pending.Clear();
            DiscardFrameViewHistory();
            DiscardOutputCompletions();
            Array.Clear(DrainedFrameOpsBuffer);
            Array.Clear(DrainedTextureUploadFrameOpsBuffer);
        }
    }

    public void Dispose()
    {
        using (SyncRoot.EnterScope())
        {
            FailPendingSubmissionMarkers(CollectionsMarshal.AsSpan(Pending));
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                CollectionsMarshal.AsSpan(Pending));
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                DrainedFrameOpsBuffer);
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                DrainedTextureUploadFrameOpsBuffer);
            Pending.Clear();
            DiscardFrameViewHistory();
            DiscardOutputCompletions();
        }

        _threadWorkspace.Dispose();
    }

    private static void FailPendingSubmissionMarkers(ReadOnlySpan<FrameOp> operations)
    {
        for (int index = 0; index < operations.Length; index++)
            if (operations[index] is SubmissionMarkerOp marker)
                marker.Fence.Fail();
    }

    private bool TryGetFrameViewHistoryEntry(in RenderFrameViewHistoryBackendReservation reservation, out int index)
    {
        index = reservation.BackendSlot;
        RenderFrameViewHistoryCandidateToken candidate = reservation.Candidate;
        return reservation.IsValid && (uint)index < (uint)_frameViewHistory.Length &&
            _frameViewHistory[index].State == EFrameViewHistoryState.Reserved &&
            _frameViewHistoryGenerations[index] == reservation.BackendGeneration &&
            _frameViewHistory[index].Reservation.Candidate.MatchesIdentity(
                in candidate) &&
            _frameViewHistory[index].Reservation.Output.Equals(
                reservation.Output) &&
            ReferenceEquals(
                _frameViewHistory[index].Reservation.TargetFrameBuffer,
                reservation.TargetFrameBuffer);
    }

    private void DiscardFrameViewHistory()
    {
        for (int index = 0; index < _frameViewHistory.Length; index++)
        {
            ref FrameViewHistoryEntry entry = ref _frameViewHistory[index];
            if (entry.State != EFrameViewHistoryState.Empty)
                entry.Reservation.Candidate.Discard();
            entry = default;
        }
        for (int index = 0; index < DrainedFrameViewHistoryCount; index++)
            _drainedFrameViewHistory[index].Candidate.Discard();
        _drainedFrameViewHistory.AsSpan(0, DrainedFrameViewHistoryCount).Clear();
        DrainedFrameViewHistoryCount = 0;
    }

    private bool TryGetOutputCompletionEntry(
        in RenderOutputCompletionBackendReservation reservation,
        out int index)
    {
        index = reservation.BackendSlot;
        return reservation.IsValid &&
            (uint)index < (uint)_outputCompletions.Length &&
            _outputCompletions[index].State == EOutputCompletionState.Reserved &&
            _outputCompletionGenerations[index] == reservation.BackendGeneration &&
            _outputCompletions[index].Reservation.ReceiptId == reservation.ReceiptId &&
            _outputCompletions[index].Reservation.Output.Equals(reservation.Output) &&
            ReferenceEquals(
                _outputCompletions[index].Reservation.TargetFrameBuffer,
                reservation.TargetFrameBuffer);
    }

    private void DiscardOutputCompletions()
    {
        for (int index = 0; index < _outputCompletions.Length; index++)
        {
            ref OutputCompletionEntry entry = ref _outputCompletions[index];
            if (entry.State != EOutputCompletionState.Empty)
                FailOutputCompletionFence(entry.Reservation.Fence);
            entry = default;
        }
        for (int index = 0; index < DrainedOutputCompletionCount; index++)
            FailOutputCompletionFence(_drainedOutputCompletions[index].Fence);
        ReleaseDrainedOutputCompletionOwnership();
    }

    private static void FailOutputCompletionFence(XRGpuFence? fence)
    {
        if (fence is VulkanTimelineGpuFence timelineFence)
            timelineFence.Fail();
        else
            fence?.Dispose();
    }

    private static uint NextNonZero(uint value) => value == uint.MaxValue ? 1U : value + 1U;

    private struct FrameViewHistoryEntry
    {
        internal RenderFrameViewHistoryBackendReservation Reservation;
        internal EFrameViewHistoryState State;
    }

    private enum EFrameViewHistoryState : byte { Empty, Reserved, Ready }

    private struct OutputCompletionEntry
    {
        internal RenderOutputCompletionBackendReservation Reservation;
        internal EOutputCompletionState State;
    }

    private enum EOutputCompletionState : byte { Empty, Reserved, Ready }

    private static VulkanAdvancedVisibilityInputLease[]
        CreateAdvancedVisibilityInputLeases()
    {
        VulkanAdvancedVisibilityInputLease[] leases =
            new VulkanAdvancedVisibilityInputLease[
                AdvancedVisibilityLeaseCapacity];
        for (int index = 0; index < leases.Length; ++index)
            leases[index] = new();
        return leases;
    }

    internal sealed class ThreadWorkspace
    {
        public FrameOpCapture? Capture;
        public FrameOpCapture? CaptureScratch;
        public FrameOpCapture? OrderedComputeBatchCapture;
        public FrameOpCapture? OrderedComputeBatchCaptureScratch;
        public Dictionary<int, FrameOp[]> CaptureBuffersByCount { get; } = [];
        public int RenderQueryBracketDepth;

        public void Reset()
        {
            Capture = null;
            CaptureScratch = null;
            OrderedComputeBatchCapture = null;
            OrderedComputeBatchCaptureScratch = null;
            CaptureBuffersByCount.Clear();
            RenderQueryBracketDepth = 0;
        }
    }
}
