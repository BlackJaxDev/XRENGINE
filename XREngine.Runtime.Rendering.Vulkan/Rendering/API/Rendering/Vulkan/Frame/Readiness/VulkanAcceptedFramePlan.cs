using XREngine.Rendering.Shadows;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Commands;

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
    private const int AuthoredOperationCapacity = StaticCapacity + UiCapacity;

    private readonly FrameOp[] _authoredOperations = new FrameOp[AuthoredOperationCapacity];
    private readonly FrameOp[] _authoredTextureUploadOperations = new FrameOp[UploadCapacity];
    private readonly FrameOp[] _staticOperations = new FrameOp[StaticCapacity];
    private readonly FrameOp[] _dynamicUiOperations = new FrameOp[UiCapacity];
    private readonly FrameOp[] _textureUploadOperations = new FrameOp[UploadCapacity];
    private readonly XRTexture?[] _requiredTextures = new XRTexture?[UploadCapacity];
    private readonly long[] _requiredTextureGenerations = new long[UploadCapacity];
    private readonly VulkanBindlessMaterialTextureReceipt[] _bindlessTextureReceipts =
        new VulkanBindlessMaterialTextureReceipt[UploadCapacity];
    private readonly GPUMaterialTextureReference[] _bindlessDescriptorReferences =
        new GPUMaterialTextureReference[UploadCapacity];
    private readonly VulkanFrameDependencyTicket[] _dependencies =
        new VulkanFrameDependencyTicket[DependencyCapacity];
    private readonly VulkanTimelineGpuFence?[] _submissionMarkers =
        new VulkanTimelineGpuFence?[StaticCapacity + UiCapacity + UploadCapacity];
    private readonly int[] _dependencyIndex = new int[DependencyIndexCapacity];
    private readonly int[] _dependencyIndexSlots = new int[DependencyCapacity];
    private readonly RenderFrameViewHistoryBackendReservation[] _frameViewHistory =
        new RenderFrameViewHistoryBackendReservation[TerminalCapacity];
    private readonly int[] _frameViewHistoryOutputIndices = new int[TerminalCapacity];
    private readonly bool[] _frameViewHistoryAttested = new bool[TerminalCapacity];
    private readonly RenderOutputCompletionBackendReservation[] _outputCompletions =
        new RenderOutputCompletionBackendReservation[TerminalCapacity];
    private readonly int[] _outputCompletionOutputIndices = new int[TerminalCapacity];
    private readonly bool[] _outputCompletionTerminalAttested = new bool[TerminalCapacity];
    private readonly XRRenderPipelineInstance?[] _recordedAdvancedPickingPipelines =
        new XRRenderPipelineInstance?[TerminalCapacity];
    private readonly AdvancedGpuScenePublication[] _recordedAdvancedPickingPublications =
        new AdvancedGpuScenePublication[TerminalCapacity];
    private readonly ulong[] _recordedAdvancedPickingResourceGenerations =
        new ulong[TerminalCapacity];
    private FramePlan? _logicalPlan;
    private int _dependencyIndexSlotCount;
    private int _submissionMarkerCount;
    private bool _submissionMarkerOwnershipTransferred;
    private VulkanDescriptorManager? _bindlessReceiptLeaseOwner;
    private int _bindlessReceiptCount;
    private int _bindlessDescriptorReferenceCount;
    private int _authoredOperationCount;
    private int _authoredTextureUploadOperationCount;
    private int _frameViewHistoryCount;
    private bool _frameViewHistoryClaimed;
    private int _outputCompletionCount;
    private bool _outputCompletionsClaimed;
    private int _recordedAdvancedPickingSourceCount;

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
    internal Span<FrameOp> AuthoredOperations
        => _authoredOperations.AsSpan(0, _authoredOperationCount);
    internal ReadOnlySpan<XRTexture?> RequiredTextures
        => _requiredTextures.AsSpan(0, RequiredTextureCount);
    internal ReadOnlySpan<long> RequiredTextureGenerations
        => _requiredTextureGenerations.AsSpan(0, RequiredTextureCount);
    internal ReadOnlySpan<RenderFrameViewHistoryBackendReservation> FrameViewHistory
        => _frameViewHistory.AsSpan(0, _frameViewHistoryCount);
    internal bool HasClaimedFrameViewHistory => _frameViewHistoryClaimed;
    internal bool HasClaimedOutputCompletions => _outputCompletionsClaimed;
    internal int OutputCompletionCount => _outputCompletionCount;
    internal ReadOnlySpan<RenderOutputCompletionBackendReservation> OutputCompletions
        => _outputCompletions.AsSpan(0, _outputCompletionCount);

    internal void CaptureOutputCompletions(
        ReadOnlySpan<RenderOutputCompletionBackendReservation> reservations)
    {
        if (_outputCompletionsClaimed)
            throw new InvalidOperationException(
                "The accepted frame already claimed its output-completion cohort.");
        if (reservations.Length > _outputCompletions.Length)
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.Terminal,
                _outputCompletions.Length,
                reservations.Length);

        for (int index = 0; index < reservations.Length; index++)
        {
            ref readonly RenderOutputCompletionBackendReservation reservation =
                ref reservations[index];
            if (!reservation.IsValid || reservation.Fence is not VulkanTimelineGpuFence)
                throw new ArgumentException(
                    "An output-completion cohort contains an invalid Vulkan receipt.",
                    nameof(reservations));
            for (int prior = 0; prior < index; prior++)
                if (_outputCompletions[prior].ReceiptId == reservation.ReceiptId)
                    throw new ArgumentException(
                        "An output-completion receipt was published more than once.",
                        nameof(reservations));

            _outputCompletions[index] = reservation;
            _outputCompletionOutputIndices[index] = -1;
            _outputCompletionTerminalAttested[index] = false;
        }
        _outputCompletionCount = reservations.Length;
        _outputCompletionsClaimed = true;
    }

    private void BindOutputCompletions(FramePlan logicalPlan)
    {
        FrameOperationSequence staticOperations =
            logicalPlan.GetNativeStaticOperationsForRecording();
        FrameOperationSequence dynamicOperations =
            logicalPlan.GetNativeDynamicOverlayOperationsForRecording();
        FrameOperationSequence uploadOperations =
            logicalPlan.GetNativeTextureUploadOperationsForRecording();
        for (int receiptIndex = 0; receiptIndex < _outputCompletionCount; receiptIndex++)
        {
            ref readonly RenderOutputCompletionBackendReservation receipt =
                ref _outputCompletions[receiptIndex];
            int terminalOutputIndex = -1;
            int matchedOperationCount = 0;
            bool cohortInvalid = false;
            bool hasExactTerminalOperation = false;
            int authoredOperationCount = 0;
            string bindingFailure = "none";
            ulong expectedGeneration = 0UL;
            ulong actualGeneration = 0UL;
            uint expectedDisplayWidth = 0U;
            uint expectedDisplayHeight = 0U;
            uint actualDisplayWidth = 0U;
            uint actualDisplayHeight = 0U;
            uint expectedInternalWidth = 0U;
            uint expectedInternalHeight = 0U;
            uint actualInternalWidth = 0U;
            uint actualInternalHeight = 0U;
            for (int authoredIndex = 0;
                 authoredIndex < StaticOperationCount;
                 authoredIndex++)
                if (_staticOperations[authoredIndex].ContextReference
                        .OutputCompletionReceiptId == receipt.ReceiptId)
                    authoredOperationCount++;
            for (int authoredIndex = 0;
                 authoredIndex < DynamicUiOperationCount;
                 authoredIndex++)
                if (_dynamicUiOperations[authoredIndex].ContextReference
                        .OutputCompletionReceiptId == receipt.ReceiptId)
                    cohortInvalid = true;
            for (int authoredIndex = 0;
                 authoredIndex < TextureUploadOperationCount;
                 authoredIndex++)
                if (_textureUploadOperations[authoredIndex].ContextReference
                        .OutputCompletionReceiptId == receipt.ReceiptId)
                    cohortInvalid = true;
            for (int operationIndex = 0; operationIndex < staticOperations.Length; operationIndex++)
            {
                ref readonly FrameOpContext context =
                    ref staticOperations.GetContext(operationIndex);
                if (context.OutputCompletionReceiptId != receipt.ReceiptId)
                    continue;
                if (context.OutputCompletionSourceFrame != receipt.Output.FrameId ||
                    !logicalPlan.TryResolveExecutableOutputIndex(in context, out int outputIndex))
                {
                    bindingFailure = "source frame or executable output resolution";
                    cohortInvalid = true;
                    break;
                }
                ref readonly OutputRequest output = ref logicalPlan.GetOutput(outputIndex);
                RenderOutputRequest schedulingRequest = output.SchedulingRequest;
                RenderOutputRequest receiptRequest = receipt.Output;
                RenderOutputRequest graphRequest = logicalPlan.GetOutputRequest(outputIndex);
                if (!HasValidLoweredOutput(in schedulingRequest, in output, in graphRequest))
                {
                    bindingFailure = DescribeLoweredOutputMismatch(
                        in schedulingRequest,
                        in output,
                        in graphRequest);
                    expectedGeneration = schedulingRequest.Target.TargetGeneration;
                    actualGeneration = output.ResourceGeneration;
                    expectedDisplayWidth = schedulingRequest.Target.DisplayWidth;
                    expectedDisplayHeight = schedulingRequest.Target.DisplayHeight;
                    actualDisplayWidth = output.DisplayWidth;
                    actualDisplayHeight = output.DisplayHeight;
                    expectedInternalWidth = schedulingRequest.Target.InternalWidth;
                    expectedInternalHeight = schedulingRequest.Target.InternalHeight;
                    actualInternalWidth = output.InternalWidth;
                    actualInternalHeight = output.InternalHeight;
                    cohortInvalid = true;
                    break;
                }

                matchedOperationCount++;
                if (!ReferenceEquals(
                        staticOperations.GetTarget(operationIndex),
                        receipt.TargetFrameBuffer) ||
                    !MatchesRawSchedulingContract(in receiptRequest, in schedulingRequest) ||
                    !HasValidLoweredOutput(in receiptRequest, in output, in graphRequest))
                    continue;

                if (terminalOutputIndex >= 0 && terminalOutputIndex != outputIndex)
                {
                    bindingFailure = "multiple exact terminal output indices";
                    terminalOutputIndex = -1;
                    hasExactTerminalOperation = false;
                    cohortInvalid = true;
                    break;
                }
                terminalOutputIndex = outputIndex;
                hasExactTerminalOperation = true;
            }

            if (ContainsOutputCompletionReceipt(dynamicOperations, receipt.ReceiptId) ||
                ContainsOutputCompletionReceipt(uploadOperations, receipt.ReceiptId) ||
                 cohortInvalid || matchedOperationCount == 0 ||
                 matchedOperationCount != authoredOperationCount ||
                 !hasExactTerminalOperation)
            {
                Debug.VulkanWarningEvery("Vulkan.OutputCompletion.BindingFailure", TimeSpan.FromSeconds(1),
                    "[Vulkan.OutputCompletion] Receipt {0} binding rejected: reason={1}, invalid={2}, matched={3}, authored={4}, exactTerminal={5}, generation={6}/{7}, display={8}x{9}/{10}x{11}, internal={12}x{13}/{14}x{15} (expected/actual).",
                    receipt.ReceiptId, bindingFailure, cohortInvalid, matchedOperationCount, authoredOperationCount, hasExactTerminalOperation,
                    expectedGeneration, actualGeneration,
                    expectedDisplayWidth, expectedDisplayHeight,
                    actualDisplayWidth, actualDisplayHeight,
                    expectedInternalWidth, expectedInternalHeight,
                    actualInternalWidth, actualInternalHeight);
                FailOutputCompletionFence(receipt.Fence);
                continue;
            }
            _outputCompletionOutputIndices[receiptIndex] = terminalOutputIndex;
        }
    }

    private static bool ContainsOutputCompletionReceipt(
        FrameOperationSequence operations,
        ulong receiptId)
    {
        for (int index = 0; index < operations.Length; index++)
            if (operations.GetContext(index).OutputCompletionReceiptId == receiptId)
                return true;
        return false;
    }

    internal void MarkOutputCompletionTerminalRecorded(
        int outputIndex,
        ulong receiptId,
        ulong sourceFrame,
        XRFrameBuffer? actualTarget,
        ERenderOutputWriteAspect actualWriteAspect)
    {
        for (int index = 0; index < _outputCompletionCount; index++)
        {
            ref readonly RenderOutputCompletionBackendReservation receipt =
                ref _outputCompletions[index];
            if (_outputCompletionOutputIndices[index] == outputIndex &&
                receipt.ReceiptId == receiptId &&
                receipt.Output.FrameId == sourceFrame &&
                receipt.Output.ExpectedWriteAspect == actualWriteAspect &&
                ReferenceEquals(receipt.TargetFrameBuffer, actualTarget))
                _outputCompletionTerminalAttested[index] = true;
        }
    }

    internal bool IsOutputCompletionBound(ulong receiptId)
    {
        for (int index = 0; index < _outputCompletionCount; index++)
            if (_outputCompletions[index].ReceiptId == receiptId)
                return _outputCompletionOutputIndices[index] >= 0;
        return false;
    }

    internal bool TryGetOutputCompletionRecordingState(
        int index,
        out RenderOutputCompletionBackendReservation reservation,
        out bool bound,
        out bool terminalAttested)
    {
        if ((uint)index >= (uint)_outputCompletionCount)
        {
            reservation = default;
            bound = false;
            terminalAttested = false;
            return false;
        }
        reservation = _outputCompletions[index];
        bound = _outputCompletionOutputIndices[index] >= 0;
        terminalAttested = _outputCompletionTerminalAttested[index];
        return true;
    }

    /// <summary>Claims drained queue-owned history receipts for this accepted frame.</summary>
    internal void CaptureFrameViewHistory(
        ReadOnlySpan<RenderFrameViewHistoryBackendReservation> reservations)
    {
        if (_frameViewHistoryClaimed)
            throw new InvalidOperationException("The accepted frame already claimed its history cohort.");
        int uniqueCount = 0;
        for (int index = 0; index < reservations.Length; index++)
        {
            if (!reservations[index].IsValid ||
                !reservations[index].Output.IsDefined)
            {
                throw new ArgumentException(
                    "A history cohort contains an invalid backend reservation.",
                    nameof(reservations));
            }

            bool duplicate = false;
            RenderFrameViewHistoryCandidateToken candidateToken =
                reservations[index].Candidate;
            for (int priorIndex = 0; priorIndex < index; priorIndex++)
            {
                RenderFrameViewHistoryCandidateToken priorToken =
                    reservations[priorIndex].Candidate;
                if (!priorToken.MatchesIdentity(in candidateToken))
                {
                    continue;
                }

                if (!reservations[priorIndex].Output.Equals(
                        reservations[index].Output) ||
                    !ReferenceEquals(
                        reservations[priorIndex].TargetFrameBuffer,
                        reservations[index].TargetFrameBuffer))
                {
                    throw new ArgumentException(
                        "One history token was reserved for conflicting outputs.",
                        nameof(reservations));
                }
                duplicate = true;
                break;
            }

            if (!duplicate)
                uniqueCount++;
        }
        if (_frameViewHistoryCount + uniqueCount > _frameViewHistory.Length)
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.Terminal,
                _frameViewHistory.Length,
                _frameViewHistoryCount + uniqueCount);
        for (int index = 0; index < reservations.Length; index++)
        {
            bool duplicate = false;
            RenderFrameViewHistoryCandidateToken candidateToken =
                reservations[index].Candidate;
            for (int priorIndex = 0; priorIndex < index; priorIndex++)
            {
                RenderFrameViewHistoryCandidateToken priorToken =
                    reservations[priorIndex].Candidate;
                if (!priorToken.MatchesIdentity(in candidateToken))
                {
                    continue;
                }
                duplicate = true;
                break;
            }
            if (duplicate)
                continue;

            _frameViewHistory[_frameViewHistoryCount] = reservations[index];
            _frameViewHistoryOutputIndices[_frameViewHistoryCount] = -1;
            _frameViewHistoryCount++;
        }
        _frameViewHistoryClaimed = true;
    }

    /// <summary>Associates candidates with an executable exact output identity.</summary>
    internal void BindFrameViewHistory(FramePlan logicalPlan)
    {
        for (int candidateIndex = 0; candidateIndex < _frameViewHistoryCount; candidateIndex++)
        {
            ref readonly RenderFrameViewHistoryBackendReservation candidate =
                ref _frameViewHistory[candidateIndex];
            int matchedOutput = -1;
            bool schedulingContractFound = false;
            bool loweredOutputFound = false;
            bool producerCohortFound = false;
            for (int outputIndex = 0; outputIndex < logicalPlan.OutputCount; outputIndex++)
            {
                if (!logicalPlan.GetOutputDecision(outputIndex).Execute)
                    continue;
                ref readonly OutputRequest output =
                    ref logicalPlan.GetOutput(outputIndex);
                ref readonly RenderOutputRequest graphOutput =
                    ref logicalPlan.GetOutputRequest(outputIndex);
                RenderOutputRequest candidateOutput = candidate.Output;
                RenderOutputRequest schedulingOutput =
                    output.SchedulingRequest;
                if (!MatchesRawSchedulingContract(
                        in candidateOutput,
                        in schedulingOutput))
                    continue;
                schedulingContractFound = true;
                if (!HasValidLoweredOutput(
                        in candidateOutput,
                        in output,
                        in graphOutput))
                    continue;
                loweredOutputFound = true;
                if (!HasExactProducerCohort(
                        logicalPlan,
                        outputIndex,
                        in candidate))
                    continue;
                producerCohortFound = true;
                matchedOutput = outputIndex;
                break;
            }
            _frameViewHistoryOutputIndices[candidateIndex] = matchedOutput;
            if (matchedOutput < 0)
            {
                if (Debug.ShouldLogEvery(
                        "Vulkan.FrameViewHistory.BindRejected",
                        TimeSpan.FromSeconds(1)))
                {
                    Debug.VulkanWarning(
                        "[Vulkan][FrameViewHistory] Candidate binding rejected. Sequence={0} SourceFrame={1} OutputId={2} Outputs={3} SchedulingMatch={4} LoweredMatch={5} ProducerCohortMatch={6} TargetIsDefault={7}.",
                        candidate.Candidate.Sequence,
                        candidate.Candidate.SourceFrame,
                        candidate.Output.OutputId,
                        logicalPlan.OutputCount,
                        schedulingContractFound,
                        loweredOutputFound,
                        producerCohortFound,
                        candidate.TargetFrameBuffer is null);
                }
                candidate.Candidate.Discard();
            }
            else
            {
                RenderFrameViewHistoryCandidateToken candidateToken = candidate.Candidate;
                for (int priorIndex = 0; priorIndex < candidateIndex; priorIndex++)
                {
                    if (_frameViewHistoryOutputIndices[priorIndex] != matchedOutput)
                        continue;
                    RenderFrameViewHistoryCandidateToken prior =
                        _frameViewHistory[priorIndex].Candidate;
                    if (!prior.SharesLedger(in candidateToken))
                        continue;
                    if (prior.Sequence >= candidateToken.Sequence)
                    {
                        candidateToken.Discard();
                        _frameViewHistoryOutputIndices[candidateIndex] = -1;
                        break;
                    }
                    prior.Discard();
                    _frameViewHistoryOutputIndices[priorIndex] = -1;
                }
            }
        }
    }

    /// <summary>Marks one exact output terminal write as observed during primary recording.</summary>
    internal void MarkFrameViewHistoryOutputRecorded(
        int outputIndex,
        ulong historySequence,
        ulong sourceFrame,
        XRFrameBuffer? actualTarget)
    {
        for (int index = 0; index < _frameViewHistoryCount; index++)
        {
            ref readonly RenderFrameViewHistoryBackendReservation reservation =
                ref _frameViewHistory[index];
            if (_frameViewHistoryOutputIndices[index] == outputIndex &&
                reservation.Candidate.Sequence == historySequence &&
                reservation.Candidate.SourceFrame == sourceFrame &&
                ReferenceEquals(reservation.TargetFrameBuffer, actualTarget))
            {
                _frameViewHistoryAttested[index] = true;
            }
        }
    }

    /// <summary>
    /// Attests all exact candidate aliases explicitly declared for the one
    /// synthetic default-target clear. No active operation context is inferred.
    /// </summary>
    internal void MarkFreshEmptyFrameViewHistoryOutputsRecorded(
        FramePlan logicalPlan)
    {
        for (int freshIndex = 0;
             freshIndex < logicalPlan.FreshEmptyTerminalOutputCount;
             freshIndex++)
        {
            int outputIndex =
                logicalPlan.GetFreshEmptyTerminalOutputIndex(freshIndex);
            for (int candidateIndex = 0;
                 candidateIndex < _frameViewHistoryCount;
                 candidateIndex++)
            {
                if (_frameViewHistoryOutputIndices[candidateIndex] == outputIndex &&
                    _frameViewHistory[candidateIndex].TargetFrameBuffer is null)
                {
                    _frameViewHistoryAttested[candidateIndex] = true;
                }
            }
        }
    }

    /// <summary>
    /// Replays one structurally proven output write from a reusable primary.
    /// Candidate sequence/source proof comes from binding against the current
    /// accepted logical plan, never from the older artifact's source frame.
    /// </summary>
    internal void MarkReusableFrameViewHistoryOutputRecorded(
        FramePlan logicalPlan,
        int outputIndex,
        in OutputRequest recordedOutput,
        XRFrameBuffer? recordedTarget)
    {
        int resolvedOutputIndex = -1;
        if ((uint)outputIndex < (uint)logicalPlan.OutputCount &&
            logicalPlan.GetOutputDecision(outputIndex).Execute)
        {
            ref readonly OutputRequest indexed =
                ref logicalPlan.GetOutput(outputIndex);
            if (MatchesStructuralOutput(in recordedOutput, in indexed))
                resolvedOutputIndex = outputIndex;
        }

        for (int index = 0; index < logicalPlan.OutputCount; index++)
        {
            if (index == outputIndex ||
                !logicalPlan.GetOutputDecision(index).Execute)
            {
                continue;
            }

            ref readonly OutputRequest candidate =
                ref logicalPlan.GetOutput(index);
            if (!MatchesStructuralOutput(in recordedOutput, in candidate))
                continue;
            if (resolvedOutputIndex >= 0)
                return;
            resolvedOutputIndex = index;
        }
        if (resolvedOutputIndex < 0)
            return;

        for (int index = 0; index < _frameViewHistoryCount; index++)
            if (_frameViewHistoryOutputIndices[index] == resolvedOutputIndex &&
                ReferenceEquals(
                    _frameViewHistory[index].TargetFrameBuffer,
                    recordedTarget))
                _frameViewHistoryAttested[index] = true;
    }

    /// <summary>Publishes only bound, attested candidates after native submission acceptance.</summary>
    internal void CommitAttestedFrameViewHistory()
    {
        for (int index = 0; index < _frameViewHistoryCount; index++)
        {
            if (_frameViewHistoryOutputIndices[index] >= 0 && _frameViewHistoryAttested[index])
                _frameViewHistory[index].Candidate.Commit();
            else
                _frameViewHistory[index].Candidate.Discard();
            _frameViewHistory[index] = default;
            _frameViewHistoryOutputIndices[index] = -1;
            _frameViewHistoryAttested[index] = false;
        }
        _frameViewHistoryCount = 0;
    }

    internal void MarkAdvancedPickingVisibilityRecorded(
        XRRenderPipelineInstance pipeline,
        in AdvancedGpuScenePublication publication,
        ulong resourceGeneration)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (!publication.IsValid || resourceGeneration == 0UL)
            throw new VulkanPlanPreconditionException(
                "A recorded Advanced visibility raster exposed an invalid canonical picking source.");

        for (int index = 0; index < _recordedAdvancedPickingSourceCount; index++)
        {
            if (!ReferenceEquals(_recordedAdvancedPickingPipelines[index], pipeline))
                continue;
            if (_recordedAdvancedPickingPublications[index] != publication ||
                _recordedAdvancedPickingResourceGenerations[index] != resourceGeneration)
            {
                throw new VulkanPlanPreconditionException(
                    "One accepted pipeline recorded multiple canonical Advanced picking publications in the same primary.");
            }
            return;
        }

        if (_recordedAdvancedPickingSourceCount >=
            _recordedAdvancedPickingPipelines.Length)
        {
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.MainScene,
                _recordedAdvancedPickingPipelines.Length,
                _recordedAdvancedPickingSourceCount + 1);
        }

        int destination = _recordedAdvancedPickingSourceCount++;
        _recordedAdvancedPickingPipelines[destination] = pipeline;
        _recordedAdvancedPickingPublications[destination] = publication;
        _recordedAdvancedPickingResourceGenerations[destination] = resourceGeneration;
    }

    /// <summary>
    /// Makes a canonical publication eligible for picking only after the primary
    /// containing its actual visibility raster was accepted by the native queue.
    /// </summary>
    internal void CommitRecordedAdvancedPickingSources()
    {
        for (int index = 0; index < _recordedAdvancedPickingSourceCount; index++)
        {
            _recordedAdvancedPickingPipelines[index]?.CommitAdvancedPickingSource(
                in _recordedAdvancedPickingPublications[index],
                _recordedAdvancedPickingResourceGenerations[index]);
            _recordedAdvancedPickingPipelines[index] = null;
            _recordedAdvancedPickingPublications[index] = default;
            _recordedAdvancedPickingResourceGenerations[index] = 0UL;
        }
        _recordedAdvancedPickingSourceCount = 0;
    }

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
        for (int index = 0; index < _outputCompletionCount; index++)
            _outputCompletions[index] = default;
        _outputCompletionCount = 0;
    }

    /// <summary>Fails every marker still owned by this unsubmitted plan once.</summary>
    internal void SettleUnsubmittedSubmissionMarkers()
    {
        if (_submissionMarkerOwnershipTransferred)
            return;

        for (int index = 0; index < _submissionMarkerCount; index++)
            _submissionMarkers[index]?.Fail();
        _submissionMarkers.AsSpan(0, _submissionMarkerCount).Clear();
        _submissionMarkerCount = 0;
        for (int index = 0; index < _outputCompletionCount; index++)
            FailOutputCompletionFence(_outputCompletions[index].Fence);
        _outputCompletions.AsSpan(0, _outputCompletionCount).Clear();
        _outputCompletionOutputIndices.AsSpan(0, _outputCompletionCount).Fill(-1);
        _outputCompletionTerminalAttested.AsSpan(0, _outputCompletionCount).Clear();
        _outputCompletionCount = 0;
        _outputCompletionsClaimed = false;
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

    /// <summary>
    /// Claims operations authored before this attempt reaches its final logical
    /// seal. A rejected attempt releases this cohort instead of leaving it in the
    /// shared queue for an unrelated successor.
    /// </summary>
    internal void CaptureAuthoredOperations(
        ReadOnlySpan<FrameOp> operations,
        ReadOnlySpan<FrameOp> textureUploadOperations)
    {
        if (IsSealed)
            throw new InvalidOperationException("The accepted frame plan is already sealed.");
        if (_authoredOperationCount + operations.Length > _authoredOperations.Length)
        {
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.MainScene,
                _authoredOperations.Length,
                _authoredOperationCount + operations.Length);
        }
        if (_authoredTextureUploadOperationCount + textureUploadOperations.Length >
            _authoredTextureUploadOperations.Length)
        {
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.Upload,
                _authoredTextureUploadOperations.Length,
                _authoredTextureUploadOperationCount + textureUploadOperations.Length);
        }

        operations.CopyTo(
            _authoredOperations.AsSpan(_authoredOperationCount));
        _authoredOperationCount += operations.Length;
        textureUploadOperations.CopyTo(
            _authoredTextureUploadOperations.AsSpan(
                _authoredTextureUploadOperationCount));
        _authoredTextureUploadOperationCount += textureUploadOperations.Length;

        ClaimUnsubmittedSubmissionMarkers(operations);
        ClaimUnsubmittedSubmissionMarkers(textureUploadOperations);
    }

    /// <summary>Transfers the attempt-owned authored cohort into its final lanes.</summary>
    internal void TransferAuthoredOperations(
        ReadOnlySpan<FrameOp> staticOperations,
        ReadOnlySpan<FrameOp> dynamicUiOperations)
    {
        CaptureOperations(
            staticOperations,
            dynamicUiOperations,
            _authoredTextureUploadOperations.AsSpan(
                0,
                _authoredTextureUploadOperationCount));

        _authoredOperations.AsSpan(0, _authoredOperationCount).Clear();
        _authoredTextureUploadOperations.AsSpan(
            0,
            _authoredTextureUploadOperationCount).Clear();
        _authoredOperationCount = 0;
        _authoredTextureUploadOperationCount = 0;
    }

    internal void CaptureOperations(
        ReadOnlySpan<FrameOp> staticOperations,
        ReadOnlySpan<FrameOp> dynamicUiOperations,
        ReadOnlySpan<FrameOp> textureUploadOperations)
    {
        if (IsSealed)
            throw new InvalidOperationException("The accepted frame plan is already sealed.");

        int terminalOperationCount = 0;
        int mainSceneOperationCount = 0;
        int shadowOperationCount = 0;

        for (int index = 0; index < staticOperations.Length; index++)
        {
            FrameOp operation = staticOperations[index];
            EVulkanAcceptedFrameLane lane = ClassifyStaticOperation(operation);
            int laneCount = lane switch
            {
                EVulkanAcceptedFrameLane.Terminal => ++terminalOperationCount,
                EVulkanAcceptedFrameLane.Shadow => ++shadowOperationCount,
                _ => ++mainSceneOperationCount,
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
        }

        if (staticOperations.Length > _staticOperations.Length)
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.MainScene,
                _staticOperations.Length,
                staticOperations.Length);
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

            staticOperations.CopyTo(_staticOperations);
            StaticOperationCount = staticOperations.Length;
            TerminalOperationCount = terminalOperationCount;
            MainSceneOperationCount = mainSceneOperationCount;
            ShadowOperationCount = shadowOperationCount;
        textureUploadOperations.CopyTo(_textureUploadOperations);
        TextureUploadOperationCount = textureUploadOperations.Length;
    }

    /// <summary>
    /// Freezes texture owners directly from the raw visible mesh cohort before
    /// material-table rows or descriptor snapshots are produced. PresentNow
    /// records the descriptor generation that is already published. Pending
    /// residency promotions remain inter-frame work and become visible after
    /// their atomic descriptor publication.
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
                includePendingUploadGeneration: false);
            CaptureMaterialTextureReferences(
                request.ResolvedMaterial.ShadowUniformSourceMaterial,
                includePendingUploadGeneration: false);
            CaptureMaterialTextureReferences(
                request.MaterialOverride,
                includePendingUploadGeneration: false);
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
        _bindlessDescriptorReferences.AsSpan(0, _bindlessDescriptorReferenceCount).Clear();
        _bindlessDescriptorReferenceCount = 0;
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
        else
            CaptureBindlessMaterialTextureReferences(descriptorManager,
                _bindlessDescriptorReferences.AsSpan(0, _bindlessDescriptorReferenceCount));
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
                    CaptureBindlessMaterialDescriptorClosure(
                        indirect.BindlessMaterialTextures,
                        ref referencesBindlessTable);
                    break;
                case EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount:
                    ref readonly MeshTaskDispatchIndirectCountPayload meshTask =
                        ref operations.GetMeshTask(index);
                    CaptureSnapshotTextureReferences(
                        meshTask.ProgramBindingSnapshot);
                    CaptureBindlessMaterialDescriptorClosure(
                        meshTask.BindlessMaterialTextures,
                        ref referencesBindlessTable);
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
                    includePendingUploadGeneration: false);
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

    private void CaptureBindlessMaterialTextureReferences(
        VulkanDescriptorManager descriptorManager,
        ReadOnlySpan<GPUMaterialTextureReference> references)
    {
        if (_bindlessReceiptCount != 0)
            return;

        if (!descriptorManager.TryAcquireGlobalMaterialTextureReceiptLeases(
                references,
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
                AddRequiredTextureReference(receipt.Texture, receipt.StreamingGeneration);
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

    private void CaptureBindlessMaterialDescriptorClosure(
        VulkanBindlessMaterialDescriptorBinding? binding,
        ref bool referencesLegacyBindlessTable)
    {
        if (binding is null)
            return;
        if (binding.Publication is not { } publication)
        {
            referencesLegacyBindlessTable = true;
            return;
        }

        foreach (GPUMaterialTextureReference reference in publication.VulkanTextureReferences)
        {
            bool alreadyCaptured = false;
            for (int index = 0; index < _bindlessDescriptorReferenceCount; ++index)
            {
                if (_bindlessDescriptorReferences[index].Equals(reference))
                {
                    alreadyCaptured = true;
                    break;
                }
            }
            if (alreadyCaptured)
                continue;
            if (_bindlessDescriptorReferenceCount >= _bindlessDescriptorReferences.Length)
                throw new VulkanAcceptedFramePlanCapacityException(
                    EVulkanAcceptedFrameLane.Upload,
                    _bindlessDescriptorReferences.Length,
                    _bindlessDescriptorReferenceCount + 1,
                    "Bindless material descriptor closure exceeds fixed accepted-frame capacity.");
            _bindlessDescriptorReferences[_bindlessDescriptorReferenceCount++] = reference;
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
        BindFrameViewHistory(logicalPlan);
        BindOutputCompletions(logicalPlan);
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

    /// <summary>
    /// Resets captured authored operations and submission markers without clearing the entire accepted plan,
    /// used when an intermediate readiness phase requires retrying before final seal.
    /// </summary>
    internal void ResetAuthoredOperations()
    {
        SettleUnsubmittedSubmissionMarkers();
        VulkanAdvancedVisibilityInputLease.ReleaseOperations(
            _authoredOperations.AsSpan(0, _authoredOperationCount));
        VulkanAdvancedVisibilityInputLease.ReleaseOperations(
            _authoredTextureUploadOperations.AsSpan(
                0,
                _authoredTextureUploadOperationCount));
        _authoredOperations.AsSpan(0, _authoredOperationCount).Clear();
        _authoredTextureUploadOperations.AsSpan(
            0,
            _authoredTextureUploadOperationCount).Clear();
        _authoredOperationCount = 0;
        _authoredTextureUploadOperationCount = 0;
    }

    internal bool TryGetFailedDependency(
        out VulkanFrameDependencyTicket failedTicket,
        out string? failureDetail)
    {
        for (int index = 0; index < DependencyCount; index++)
        {
            ref readonly VulkanFrameDependencyTicket ticket = ref _dependencies[index];
            if (ticket.State == EVulkanFrameDependencyState.TerminalFailed)
            {
                failedTicket = ticket;
                failureDetail = ticket.FailureDetail;
                return true;
            }
        }

        failedTicket = default;
        failureDetail = null;
        return false;
    }

    internal void Reset()
    {
        _recordedAdvancedPickingPipelines.AsSpan(
            0,
            _recordedAdvancedPickingSourceCount).Clear();
        _recordedAdvancedPickingPublications.AsSpan(
            0,
            _recordedAdvancedPickingSourceCount).Clear();
        _recordedAdvancedPickingResourceGenerations.AsSpan(
            0,
            _recordedAdvancedPickingSourceCount).Clear();
        _recordedAdvancedPickingSourceCount = 0;
        for (int index = 0; index < _frameViewHistoryCount; index++)
            _frameViewHistory[index].Candidate.Discard();
        _frameViewHistory.AsSpan(0, _frameViewHistoryCount).Clear();
        _frameViewHistoryOutputIndices.AsSpan(0, _frameViewHistoryCount).Fill(-1);
        _frameViewHistoryAttested.AsSpan(0, _frameViewHistoryCount).Clear();
        _frameViewHistoryCount = 0;
        _frameViewHistoryClaimed = false;
        if (!_submissionMarkerOwnershipTransferred)
            for (int index = 0; index < _outputCompletionCount; index++)
                FailOutputCompletionFence(_outputCompletions[index].Fence);
        _outputCompletions.AsSpan(0, _outputCompletionCount).Clear();
        _outputCompletionOutputIndices.AsSpan(0, _outputCompletionCount).Fill(-1);
        _outputCompletionTerminalAttested.AsSpan(0, _outputCompletionCount).Clear();
        _outputCompletionCount = 0;
        _outputCompletionsClaimed = false;
        ResetAuthoredOperations();
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
        VulkanAdvancedVisibilityInputLease.ReleaseOperations(
            _staticOperations.AsSpan(0, StaticOperationCount));
        VulkanAdvancedVisibilityInputLease.ReleaseOperations(
            _dynamicUiOperations.AsSpan(0, DynamicUiOperationCount));
        VulkanAdvancedVisibilityInputLease.ReleaseOperations(
            _textureUploadOperations.AsSpan(
                0,
                TextureUploadOperationCount));
        _staticOperations.AsSpan(0, StaticOperationCount).Clear();
        _dynamicUiOperations.AsSpan(0, DynamicUiOperationCount).Clear();
        _textureUploadOperations.AsSpan(0, TextureUploadOperationCount).Clear();
        _requiredTextures.AsSpan(0, RequiredTextureCount).Clear();
        _requiredTextureGenerations.AsSpan(0, RequiredTextureCount).Clear();
        _bindlessDescriptorReferences.AsSpan(0, _bindlessDescriptorReferenceCount).Clear();
        _bindlessDescriptorReferenceCount = 0;
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
        _authoredOperationCount = 0;
        _authoredTextureUploadOperationCount = 0;
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

    private bool HasExactProducerCohort(
        FramePlan logicalPlan,
        int outputIndex,
        in RenderFrameViewHistoryBackendReservation reservation)
    {
        RenderFrameViewHistoryCandidateToken token = reservation.Candidate;
        if (reservation.Output.FrameId != token.SourceFrame ||
            reservation.Output.OutputId == 0UL ||
            token.OutputIdentity == 0UL)
        {
            return false;
        }

        if (logicalPlan.IsFreshEmptyTerminalOutput(outputIndex))
            return reservation.TargetFrameBuffer is null;

        return HasExactProducerCohort(
                logicalPlan,
                logicalPlan.GetNativeStaticOperationsForRecording(),
                outputIndex,
                in reservation) ||
            HasExactProducerCohort(
                logicalPlan,
                logicalPlan.GetNativeDynamicOverlayOperationsForRecording(),
                outputIndex,
                in reservation);
    }

    private static bool HasExactProducerCohort(
        FramePlan logicalPlan,
        FrameOperationSequence operations,
        int outputIndex,
        in RenderFrameViewHistoryBackendReservation reservation)
    {
        RenderFrameViewHistoryCandidateToken token = reservation.Candidate;
        for (int operationIndex = 0;
             operationIndex < operations.Length;
             operationIndex++)
        {
            ref readonly FrameOpContext context =
                ref operations.GetContext(operationIndex);
            if (context.OutputHistorySequenceId != token.Sequence ||
                context.OutputHistorySourceFrame != token.SourceFrame ||
                context.OutputSchedulingInstanceIdentity != token.OutputIdentity ||
                context.PipelineInstance?.TemporalHistoryPipelineIdentity !=
                    token.PipelineIdentity ||
                !ReferenceEquals(
                    reservation.TargetFrameBuffer,
                    context.OutputFrameBuffer) ||
                !logicalPlan.TryResolveExecutableOutputIndex(
                    in context,
                    out int operationOutputIndex) ||
                operationOutputIndex != outputIndex)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static void FailOutputCompletionFence(XRGpuFence? fence)
    {
        if (fence is VulkanTimelineGpuFence timelineFence)
            timelineFence.Fail();
        else
            fence?.Dispose();
    }

    private static bool MatchesRawSchedulingContract(
        in RenderOutputRequest candidate,
        in RenderOutputRequest scheduling)
        => candidate.Equals(scheduling) ||
            candidate.OutputId == scheduling.OutputId &&
            candidate.ViewFamilyId == scheduling.ViewFamilyId &&
            candidate.OutputKind == scheduling.OutputKind &&
            candidate.ViewKind == scheduling.ViewKind &&
            candidate.OutputClass == scheduling.OutputClass &&
            candidate.Target.Equals(scheduling.Target) &&
            candidate.Schedule.Equals(scheduling.Schedule) &&
            candidate.QualityRequirements == scheduling.QualityRequirements &&
            candidate.FallbackPolicy == scheduling.FallbackPolicy &&
            candidate.CompletionRequirement == scheduling.CompletionRequirement &&
            candidate.ExpectedWriteAspect == scheduling.ExpectedWriteAspect &&
            candidate.ProducerDependencySetId == scheduling.ProducerDependencySetId &&
            candidate.ConsumerDependencySetId == scheduling.ConsumerDependencySetId &&
            candidate.FrameId == scheduling.FrameId;

    private static bool HasValidLoweredOutput(
        in RenderOutputRequest candidate,
        in OutputRequest output,
        in RenderOutputRequest graph)
        => output.StableOutputId == candidate.OutputId &&
            output.StableViewFamilyId == candidate.ViewFamilyId &&
            output.OutputKind == candidate.OutputKind &&
            output.ViewKind == candidate.ViewKind &&
            output.ResourceGeneration == candidate.Target.TargetGeneration &&
            output.DisplayWidth == candidate.Target.DisplayWidth &&
            output.DisplayHeight == candidate.Target.DisplayHeight &&
            output.InternalWidth == candidate.Target.InternalWidth &&
            output.InternalHeight == candidate.Target.InternalHeight &&
            graph.OutputId == output.StableOutputId &&
            graph.ViewFamilyId == output.StableViewFamilyId &&
            graph.OutputKind == output.OutputKind &&
            graph.ViewKind == output.ViewKind &&
            graph.OutputClass == candidate.OutputClass &&
            graph.Target.TargetClass == candidate.Target.TargetClass &&
            graph.Target.StableTargetId == output.StableOutputId &&
            graph.Target.TargetGeneration == output.ResourceGeneration &&
            graph.Target.DisplayWidth == output.DisplayWidth &&
            graph.Target.DisplayHeight == output.DisplayHeight &&
            graph.Target.InternalWidth == output.InternalWidth &&
            graph.Target.InternalHeight == output.InternalHeight &&
            graph.Target.FormatCompatibilityKey == output.ContextFingerprint &&
            graph.Target.SampleCount == candidate.Target.SampleCount &&
            graph.Target.ViewMask == candidate.Target.ViewMask &&
            graph.Target.ExternalImageSlot == candidate.Target.ExternalImageSlot &&
            graph.ProducerDependencySetId == output.ProducerDependencySetId &&
            graph.ConsumerDependencySetId == output.ConsumerDependencySetId &&
            graph.ExpectedWriteAspect == candidate.ExpectedWriteAspect;

    private static string DescribeLoweredOutputMismatch(
        in RenderOutputRequest candidate,
        in OutputRequest output,
        in RenderOutputRequest graph)
    {
        if (output.StableOutputId != candidate.OutputId)
            return "producer output id";
        if (output.StableViewFamilyId != candidate.ViewFamilyId)
            return "producer view-family id";
        if (output.OutputKind != candidate.OutputKind)
            return "producer output kind";
        if (output.ViewKind != candidate.ViewKind)
            return "producer view kind";
        if (output.ResourceGeneration != candidate.Target.TargetGeneration)
            return "producer target generation";
        if (output.DisplayWidth != candidate.Target.DisplayWidth ||
            output.DisplayHeight != candidate.Target.DisplayHeight)
            return "producer display extent";
        if (output.InternalWidth != candidate.Target.InternalWidth ||
            output.InternalHeight != candidate.Target.InternalHeight)
            return "producer internal extent";
        if (graph.OutputId != output.StableOutputId)
            return "graph output id";
        if (graph.ViewFamilyId != output.StableViewFamilyId)
            return "graph view-family id";
        if (graph.OutputKind != output.OutputKind)
            return "graph output kind";
        if (graph.ViewKind != output.ViewKind)
            return "graph view kind";
        if (graph.OutputClass != candidate.OutputClass)
            return "graph output class";
        if (graph.Target.TargetClass != candidate.Target.TargetClass)
            return "graph target class";
        if (graph.Target.StableTargetId != output.StableOutputId)
            return "graph target id";
        if (graph.Target.TargetGeneration != output.ResourceGeneration)
            return "graph target generation";
        if (graph.Target.DisplayWidth != output.DisplayWidth ||
            graph.Target.DisplayHeight != output.DisplayHeight)
            return "graph display extent";
        if (graph.Target.InternalWidth != output.InternalWidth ||
            graph.Target.InternalHeight != output.InternalHeight)
            return "graph internal extent";
        if (graph.Target.FormatCompatibilityKey != output.ContextFingerprint)
            return "graph format compatibility";
        if (graph.Target.SampleCount != candidate.Target.SampleCount)
            return "graph sample count";
        if (graph.Target.ViewMask != candidate.Target.ViewMask)
            return "graph view mask";
        if (graph.Target.ExternalImageSlot != candidate.Target.ExternalImageSlot)
            return "graph external image slot";
        if (graph.ProducerDependencySetId != output.ProducerDependencySetId)
            return "graph producer dependency set";
        if (graph.ConsumerDependencySetId != output.ConsumerDependencySetId)
            return "graph consumer dependency set";
        if (graph.ExpectedWriteAspect != candidate.ExpectedWriteAspect)
            return "graph expected write aspect";
        return "unknown lowered output field";
    }

    private static bool MatchesStructuralOutput(
        in OutputRequest candidate,
        in OutputRequest output)
        => candidate.MatchesOutput(output) &&
            candidate.DisplayWidth == output.DisplayWidth &&
            candidate.DisplayHeight == output.DisplayHeight &&
            candidate.InternalWidth == output.InternalWidth &&
            candidate.InternalHeight == output.InternalHeight &&
            candidate.ResourceGeneration == output.ResourceGeneration &&
            candidate.DescriptorGeneration == output.DescriptorGeneration &&
            candidate.ContextFingerprint == output.ContextFingerprint &&
            candidate.ProducerDependencySetId == output.ProducerDependencySetId &&
            candidate.ConsumerDependencySetId == output.ConsumerDependencySetId &&
            candidate.SchedulingRequest.Target.TargetClass ==
                output.SchedulingRequest.Target.TargetClass &&
            candidate.SchedulingRequest.Target.SampleCount ==
                output.SchedulingRequest.Target.SampleCount &&
            candidate.SchedulingRequest.Target.ViewMask ==
                output.SchedulingRequest.Target.ViewMask &&
            candidate.SchedulingRequest.Target.ExternalImageSlot ==
                output.SchedulingRequest.Target.ExternalImageSlot;

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
