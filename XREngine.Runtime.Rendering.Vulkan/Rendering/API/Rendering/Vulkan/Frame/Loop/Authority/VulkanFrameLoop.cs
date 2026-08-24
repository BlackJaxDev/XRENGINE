using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Rendering.Resources;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns desktop frame admission, attempt identity, frame-slot progression, and
/// the ordered composition of each desktop frame attempt.
/// </summary>
internal sealed partial class VulkanFrameLoop
{
    private const int DesktopFrameSlotCount = 2;
    private readonly Vk _api;
    private readonly VulkanDeviceContext _deviceContext;
    private readonly VulkanOutputRuntime _outputRuntime;
    private readonly VulkanFramePlanner _framePlanner;
    private readonly VulkanResourceRuntime _resourceRuntime;
    private readonly VulkanCommandRuntime _commandRuntime;
    private readonly VulkanResourcePlannerSessionService _resourcePlannerSessions;
    private readonly VulkanResourceGenerationTransactionService _resourceGenerationTransactions;
    private readonly VulkanFrameTelemetry _telemetry;
    private readonly IVulkanRendererTargetDriver _targetDriver;
    private readonly int _frameSlotCount;
    private readonly VulkanPrimaryCommandPlan[] _explicitPrimaryPlans;
    internal VulkanMeshOperationRequestQueue MeshOperationRequests { get; } = new();
    private readonly VulkanMeshRenderRequest[] _meshOperationRequestScratch =
        new VulkanMeshRenderRequest[VulkanMeshOperationRequestQueue.Capacity];
    private readonly VulkanMeshOperationRequest[] _meshOperationMaterializationScratch =
        new VulkanMeshOperationRequest[VulkanMeshOperationRequestQueue.Capacity];
    private readonly VulkanPreparedMeshOperationCohortEntry[] _meshOperationCohortEntryScratch =
        new VulkanPreparedMeshOperationCohortEntry[VulkanMeshOperationRequestQueue.Capacity];
    private readonly VulkanPreparedMeshOperationCohort _preparedMeshOperationCohort = new();
    private readonly VulkanResidentDrawTemplate?[] _residentTemplateHitScratch =
        new VulkanResidentDrawTemplate[VulkanMeshOperationRequestQueue.Capacity];
    private readonly VulkanResidentDrawTemplateHandle[] _residentTemplateHandleScratch =
        new VulkanResidentDrawTemplateHandle[VulkanMeshOperationRequestQueue.Capacity];
    private readonly VulkanPreparedMeshIngress _preparedMeshIngress = new();
    private readonly OpenXrMeshFrameOpCaptureEmitter _openXrMeshFrameOpCaptureEmitter;
    private FrameOpResourceUseList _preparedMeshIngressResourceUseScratch;
    private long _preparedMeshOperationCohortHits;
    private long _preparedMeshOperationCohortBuilds;
    private long _preparedMeshOperationFullMaterializations;
    private long _preparedMeshOperationReusedOperations;
    private long _preparedMeshOperationLegacyHoleMaterializations;

    internal long PreparedMeshOperationCohortHits
        => Volatile.Read(ref _preparedMeshOperationCohortHits);
    internal long PreparedMeshOperationCohortBuilds
        => Volatile.Read(ref _preparedMeshOperationCohortBuilds);
    internal long PreparedMeshOperationFullMaterializations
        => Volatile.Read(ref _preparedMeshOperationFullMaterializations);
    internal long PreparedMeshOperationReusedOperations
        => Volatile.Read(ref _preparedMeshOperationReusedOperations);
    internal long PreparedMeshOperationLegacyHoleMaterializations
        => Volatile.Read(ref _preparedMeshOperationLegacyHoleMaterializations);
    // Visibility sorting is allowed to reorder the request cohort every frame.
    // Key warm preparation by the captured compatibility identity rather than by
    // its transient queue index, otherwise camera motion repeatedly classifies
    // already-prepared Sponza meshes as cold work.
    private const int MaxWarmMeshPreparationSignatures =
        VulkanMeshOperationRequestQueue.Capacity * 4;
    private readonly HashSet<ulong> _meshOperationWarmPreparationSignatures =
        new(MaxWarmMeshPreparationSignatures);
    private readonly HashSet<ulong> _quarantinedMeshOperationSignatures =
        new(MaxWarmMeshPreparationSignatures);
    private int _meshOperationPreparationCursor;
    private VulkanImGuiBackend? _imguiBackend;
    private readonly VulkanImGuiOverlayCommandRecorder _imguiOverlayRecorder = new();
    private readonly DesktopFrameActivityState _activity = new();
    private readonly object _retirementGate = new();
    private int _frameSlot;
    private ulong _acceptedAttemptCount;
    private long _lastObservedTickTimestamp;
    private long _resourceCatchUpStartedAt;
    private ulong _resourceCatchUpBlockedFrames;
    private VulkanFrameLoopInitializationStage _initializationStage;
    private int _cleanupInProgress;
    private int _quiescing;
    private int _activeFrameExecutions;
    private readonly bool _injectResidentTemplateDeviceLoss;
    private int _residentTemplateDeviceLossInjected;

    internal VulkanFrameLoop(
        Vk api,
        VulkanDeviceContext deviceContext,
        VulkanOutputRuntime outputRuntime,
        VulkanFramePlanner framePlanner,
        VulkanResourceRuntime resourceRuntime,
        VulkanCommandRuntime commandRuntime,
        VulkanFrameTelemetry telemetry,
        IVulkanRendererTargetDriver targetDriver,
        Silk.NET.Windowing.IWindow? window)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _deviceContext = deviceContext;
        _outputRuntime = outputRuntime;
        _framePlanner = framePlanner;
        _resourceRuntime = resourceRuntime;
        _commandRuntime = commandRuntime;
        _telemetry = telemetry;
        _targetDriver = targetDriver;
        _injectResidentTemplateDeviceLoss = XREnvironment.IsEnabled(
            XREngineEnvironmentVariables.VulkanResidentTemplateDeviceLossInject);
        _openXrMeshFrameOpCaptureEmitter = new OpenXrMeshFrameOpCaptureEmitter(this);
        if (targetDriver is IVulkanExplicitFrameTargetDriver explicitTarget)
        {
            _frameSlotCount = checked((int)explicitTarget.OutputProperties.FrameSlotCount);
            _explicitPrimaryPlans = new VulkanPrimaryCommandPlan[
                _frameSlotCount];
            for (int index = 0; index < _explicitPrimaryPlans.Length; index++)
                _explicitPrimaryPlans[index] = new VulkanPrimaryCommandPlan();
        }
        else
        {
            _frameSlotCount = DesktopFrameSlotCount;
            _explicitPrimaryPlans = [];
        }
        _window = window;
        _resourcePlannerSessions = new VulkanResourcePlannerSessionService(
            framePlanner,
            commandRuntime);
        _resourceGenerationTransactions = new VulkanResourceGenerationTransactionService(
            deviceContext,
            framePlanner,
            resourceRuntime,
            _resourcePlannerSessions);
        _resourceRuntime.Samplers.PublishFrameSlot(CurrentFrameSlot);
        _resourceRuntime.Images.PublishFrameSlot(CurrentFrameSlot);
        _resourceRuntime.Buffers.PublishFrameSlot(CurrentFrameSlot);
        _resourceRuntime.Descriptors.PublishFrameSlot(CurrentFrameSlot);
        _resourceRuntime.PublishFramebufferRetirementFrameSlot(CurrentFrameSlot);
    }

    private readonly Silk.NET.Windowing.IWindow? _window;

    internal ulong AcceptedAttemptCount => Volatile.Read(ref _acceptedAttemptCount);
    internal IVulkanRendererTargetDriver TargetDriver => _targetDriver;
    internal RenderExecutionMode TargetExecutionMode => _targetDriver.ExecutionMode;
    internal bool TargetRequiresPresentQueue => _targetDriver.RequiresPresentQueue;
    internal bool TargetRequiresSwapchainOutput => _targetDriver.RequiresSwapchainOutput;
    internal bool TargetSupportsStreamlinePresentation => _targetDriver.SupportsStreamlinePresentation;
    internal bool HasExplicitFrameTarget => _targetDriver is IVulkanExplicitFrameTargetDriver;
    internal IReadOnlyList<string> TargetRequiredDeviceExtensions => _targetDriver.RequiredDeviceExtensions;
    internal string[] GetTargetRequiredInstanceExtensions() => _targetDriver.GetRequiredInstanceExtensions();

    internal IVulkanExplicitFrameTargetDriver RequireExplicitFrameTarget()
        => _targetDriver as IVulkanExplicitFrameTargetDriver
            ?? throw new InvalidOperationException(
                $"Vulkan target '{TargetExecutionMode}' does not expose explicit target-frame submission.");
    internal VulkanResourcePlannerSessionService ResourcePlannerSessions
        => _resourcePlannerSessions;
    internal VulkanResourceGenerationTransactionService ResourceGenerationTransactions
        => _resourceGenerationTransactions;
    internal VulkanImGuiPlatformWindowOutputAuthority ImGuiPlatformWindows { get; } = new();

    private VulkanImGuiBackend GetOrCreateImGuiBackendCore(
        IVulkanImGuiOutputHost outputHost,
        XRWindow windowHost)
    {
        VulkanImGuiBackend? backend = _imguiBackend;
        if (backend is not null && !ImGuiContextTracker.IsAlive(backend.ContextHandle))
        {
            backend.Dispose();
            _imguiBackend = null;
            _outputRuntime._imguiDrawData.Clear();
        }

        return _imguiBackend ??= new VulkanImGuiBackend(outputHost, windowHost);
    }
    internal bool TryReadBufferBytesForDiagnostics(
        VulkanBackendObjectContext backendContext,
        XRDataBuffer? sourceBuffer,
        uint sourceByteOffset,
        Span<byte> destination,
        out string reason)
        => TryReadBufferBytesForDiagnosticsCore(
            backendContext,
            sourceBuffer,
            sourceByteOffset,
            destination,
            out reason);
    private Vk Api => _api;
    private VulkanOutputRuntime OutputRuntime => _outputRuntime;
    private VulkanResourceRuntime ResourceRuntime => _resourceRuntime;
    private VulkanFrameTelemetry _frameTelemetry => _telemetry;
    private ResourcePlannerRuntimeState PublishedResourcePlannerRuntimeState
        => _framePlanner
            .GetPublishedResourcePlannerGeneration()
            .State;
    private ulong ResourcePlannerRevision
        => PublishedResourcePlannerRuntimeState.ResourcePlannerRevision;
    private ulong ActiveResourcePlannerSignature
        => _resourcePlannerSessions.CaptureRuntimeState().ResourcePlannerSignature;
    private ulong ActiveResourceAllocationSignature
        => _resourcePlannerSessions.CaptureRuntimeState().ResourceAllocationSignature;
    private FrameOpContext? ActiveLastActiveFrameOpContext
    {
        get => _resourcePlannerSessions.CaptureRuntimeState().LastActiveFrameOpContext;
        set
        {
            ResourcePlannerRuntimeState state = _resourcePlannerSessions.CaptureRuntimeState();
            state.LastActiveFrameOpContext = value;
            _resourcePlannerSessions.RestoreRuntimeState(state);
        }
    }
    private VulkanDesktopWsiTargetDriver DesktopWsiOutput
        => _targetDriver as VulkanDesktopWsiTargetDriver
            ?? throw new InvalidOperationException(
                $"Vulkan target '{TargetExecutionMode}' does not provide desktop WSI policy.");
    private VulkanMappedFrameArena? MappedFrameArena
        => _resourceRuntime.MappedFrameArena;
    private VulkanFrameDataArena? FrameDataArena
        => _resourceRuntime.FrameDataArena;
    private VulkanPresentationSourcePublication _windowPresentSource
        => _outputRuntime.PresentationSource.Publication;
    private ref FrameOpContext? _lastWindowPresentFrameOpContext
        => ref _outputRuntime.PresentationSource.FrameOpContext;
    private ref PrimaryCommandArtifactOwner[]? _primaryCommandArtifactOwners
        => ref _commandRuntime.CommandBuffers.PrimaryOwners;
    private ref bool[]? _commandBufferDirtyFlags
        => ref _commandRuntime.CommandBuffers.DirtyFlags;
    private ref ulong[]? _commandBufferFrameOpSignatures
        => ref _commandRuntime.CommandBuffers.FrameOpSignatures;
    private ref bool _lastEnsureCommandBufferRecordedPrimary
        => ref _commandRuntime.CommandBuffers.LastEnsureRecordedPrimary;
    private object _oneTimeSubmitLock
        => _commandRuntime.CommandBuffers.OneTimeSubmitGate;
    private ref bool _frameBufferInvalidated
        => ref _outputRuntime._desktopSwapchainPolicy.FrameBufferInvalidated;
    private bool _deviceLost => !_deviceContext.StateMachine.IsOperational;
    private bool IsDeviceLost => _deviceLost;
    private static bool VulkanFrameDiagnosticsTraceEnabled
        => XREnvironment.IsEnabled(XREngineEnvironmentVariables.VulkanRecordingDiag) ||
           XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw ||
           XREngine.Rendering.RenderDiagnosticsFlags.VkTraceSwapDraw;
    internal object RetirementGate => _retirementGate;
    internal int CurrentFrameSlot => Volatile.Read(ref _frameSlot);
    private int FrameSlotCount => _frameSlotCount;
    internal long LastObservedTickTimestamp => Volatile.Read(ref _lastObservedTickTimestamp);
    internal bool HasObservedTick => LastObservedTickTimestamp != 0;
    private bool IsQuiescing => Volatile.Read(ref _quiescing) != 0;

    internal DesktopFrameActivitySnapshot CaptureActivity()
        => _activity.Capture();

    internal bool TryEnter(out DesktopFrameIdentity identity)
    {
        lock (_retirementGate)
        {
            if (IsQuiescing)
            {
                identity = default;
                return false;
            }

            int frameSlot = CurrentFrameSlot;
            ulong frameNumber = checked(AcceptedAttemptCount + 1UL);
            if (!_activity.TryEnter(frameNumber, frameSlot, out long activityPublicationToken))
            {
                identity = default;
                return false;
            }

            Volatile.Write(ref _acceptedAttemptCount, frameNumber);
            _activeFrameExecutions++;
            identity = new DesktopFrameIdentity(
                frameNumber,
                frameSlot,
                Stopwatch.GetTimestamp(),
                activityPublicationToken);
            return true;
        }
    }

    internal void Exit(in DesktopFrameIdentity identity)
    {
        lock (_retirementGate)
        {
            if (_activity.TryExit(identity.ActivityPublicationToken))
                ExitFrameExecutionNoLock();
        }
    }

    private bool TryEnterExplicitFrameExecution()
    {
        lock (_retirementGate)
        {
            if (IsQuiescing)
                return false;

            _activeFrameExecutions++;
            return true;
        }
    }

    private void ExitExplicitFrameExecution()
    {
        lock (_retirementGate)
            ExitFrameExecutionNoLock();
    }

    private void ExitFrameExecutionNoLock()
    {
        if (_activeFrameExecutions <= 0)
            throw new InvalidOperationException("Vulkan frame-execution ownership is unbalanced.");

        _activeFrameExecutions--;
        if (_activeFrameExecutions == 0)
            Monitor.PulseAll(_retirementGate);
    }

    private void QuiesceFrameAdmissionAndWait()
    {
        lock (_retirementGate)
        {
            Volatile.Write(ref _quiescing, 1);
            while (_activeFrameExecutions != 0)
                Monitor.Wait(_retirementGate);
        }
    }

    internal void AdvanceFrameSlot(int completedFrameSlot)
    {
        int nextFrameSlot = (completedFrameSlot + 1) % FrameSlotCount;
        PublishFrameSlot(nextFrameSlot);
    }

    private void PublishFrameSlot(int frameSlot)
    {
        Volatile.Write(ref _frameSlot, frameSlot);
        _resourceRuntime.Samplers.PublishFrameSlot(frameSlot);
        _resourceRuntime.Images.PublishFrameSlot(frameSlot);
        _resourceRuntime.Buffers.PublishFrameSlot(frameSlot);
        _resourceRuntime.Descriptors.PublishFrameSlot(frameSlot);
        _resourceRuntime.PublishFramebufferRetirementFrameSlot(frameSlot);
    }

    internal void RecordObservedTick(long timestamp)
        => Volatile.Write(ref _lastObservedTickTimestamp, timestamp);

    private void RecordDesktopFrameTickObserved(long timestamp)
        => RecordObservedTick(timestamp);

    private void AdvanceDesktopFrameSlot(int completedFrameSlot)
        => AdvanceFrameSlot(completedFrameSlot);

    private void ReportReentrantDesktopFrame()
    {
        DesktopFrameActivitySnapshot active = CaptureActivity();
        Debug.VulkanEvery(
            $"Vulkan.Frame.{_telemetry.GetHashCode()}.ReentrantWindowRenderSkipped",
            TimeSpan.FromMilliseconds(250),
            "[Vulkan] Skipping reentrant desktop window render callback. ActiveFrame={0} ActiveFrameSlot={1}",
            active.FrameNumber,
            active.FrameSlot);
    }

    private void RecordDesktopFrameGap(ref VulkanFrameAttempt attempt)
    {
        long previousTimestamp = LastObservedTickTimestamp;
        if (previousTimestamp == 0)
            return;

        TimeSpan gap = Stopwatch.GetElapsedTime(
            previousTimestamp,
            attempt.StartTimestamp);
        if (gap <= TimeSpan.FromSeconds(5))
            return;

        Debug.VulkanWarning(
            $"[Vulkan] Frame {attempt.FrameNumber}: {gap.TotalSeconds:F1}s gap since the last observed desktop frame tick. " +
            $"Slot={attempt.FrameSlot} SlotTimelineValue={_commandRuntime.Synchronization._frameSlotTimelineValues?[attempt.FrameSlot]}");
    }

    private void PublishDesktopFrameTelemetry(ref VulkanFrameAttempt attempt)
    {
        if (!VulkanDesktopFramePolicy.IsAcquireFinalizationLegal(
                attempt.AcquireOwnership))
        {
            throw new InvalidOperationException(
                $"Desktop frame finalized with unresolved acquire ownership {attempt.AcquireOwnership}.");
        }

        if (!VulkanDesktopFramePolicy.IsUploadFinalizationLegal(
                attempt.UploadOwnership))
        {
            throw new InvalidOperationException(
                $"Desktop frame finalized with unresolved upload ownership {attempt.UploadOwnership}.");
        }

        TimeSpan totalFrameTime = Stopwatch.GetElapsedTime(attempt.StartTimestamp);
        attempt.Timing.SetOutputIdentity(
            unchecked((int)attempt.ImageIndex),
            _outputRuntime.Desktop.Generation);
        _resourceRuntime.PublishMappedMemoryTelemetry();
        attempt.Timing.PublishAfterFrame(
            totalFrameTime,
            attempt.TerminalResultPublished
                ? attempt.TerminalResult.Outcome
                : throw new InvalidOperationException(
                    "Desktop frame telemetry cannot publish before its terminal result."));
        attempt.AdvanceTo(EDesktopFramePhase.Finalized);
    }

    private static EVulkanFrameOutcome ResolveDesktopFrameTelemetryOutcome(
        ref VulkanFrameAttempt attempt)
    {
        if (attempt.PrimaryFailure is not null || attempt.DeferredFailure is not null)
            return EVulkanFrameOutcome.Failed;

        return attempt.Reason switch
        {
            EDesktopFrameReason.Success or EDesktopFrameReason.PresentSuboptimal =>
                EVulkanFrameOutcome.Completed,
            EDesktopFrameReason.ZeroSurface or EDesktopFrameReason.FrameSlotBusy =>
                EVulkanFrameOutcome.Skipped,
            EDesktopFrameReason.ResizePending or
            EDesktopFrameReason.ResourceGenerationBlocked or
            EDesktopFrameReason.FrameGenerationModeChanged or
            EDesktopFrameReason.AcquireNotReady or
            EDesktopFrameReason.AcquireTimeout or
            EDesktopFrameReason.AcquireOutOfDate or
            EDesktopFrameReason.RecordingDeferred or
            EDesktopFrameReason.RecordingResourceRetired or
            EDesktopFrameReason.PresentOutOfDate =>
                EVulkanFrameOutcome.Deferred,
            EDesktopFrameReason.RecordingDirtied => EVulkanFrameOutcome.Rejected,
            EDesktopFrameReason.None when attempt.Flow == EDesktopFrameFlow.Completed =>
                EVulkanFrameOutcome.Completed,
            EDesktopFrameReason.None => EVulkanFrameOutcome.Deferred,
            _ => EVulkanFrameOutcome.Failed,
        };
    }

    private static void ReportDesktopFrameTelemetryFailure(
        Exception telemetryFailure)
        => Debug.VulkanWarning(
            "[Vulkan] Desktop frame telemetry finalization failed: {0}",
            telemetryFailure.Message);

    private static VulkanDesktopFramePhaseResult CompleteDesktopFramePhase(
        ref VulkanFrameAttempt attempt,
        EVulkanFrameStage stage,
        EDesktopFrameFlow flow)
        => attempt.CompletePhase(stage, flow);

    /// <summary>
    /// Performs the single terminal ownership pass for an accepted attempt.
    /// Helpers may already have settled ownership; this method only invokes
    /// recovery when an unresolved ownership state remains.
    /// </summary>
    private Exception? SettleDesktopFrameAttempt(
        ref VulkanFrameAttempt attempt)
    {
        if (!attempt.TryClaimTerminalSettlement())
            return null;

        Exception? settlementFailure = null;
        if (!_deviceLost)
        {
            try
            {
                SettleAcceptedDesktopRecoverySubmissionDebt(ref attempt);
            }
            catch (Exception failure)
            {
                AddDesktopSettlementFailure(ref settlementFailure, failure);
            }

            try
            {
                if (attempt.UploadOwnership == EVulkanDesktopUploadOwnership.SubmittedDeferredFree)
                {
                    CommitSubmittedDesktopTextureUpload(
                        ref attempt,
                        attempt.GraphicsSignalValue,
                        "terminal desktop settlement");
                }
            }
            catch (Exception failure)
            {
                AddDesktopSettlementFailure(ref settlementFailure, failure);
            }
        }

        try
        {
            if (_deviceLost)
            {
                AbandonDesktopOwnershipAfterDeviceLoss(ref attempt);
            }
            else if (!VulkanDesktopFramePolicy.IsAcquireFinalizationLegal(attempt.AcquireOwnership))
            {
                SettleDesktopAcquireAfterUnexpectedFailure(
                    ref attempt,
                    attempt.PrimaryFailure ?? new InvalidOperationException(
                        "Desktop frame ended with unresolved native ownership."));
            }
        }
        catch (Exception failure)
        {
            AddDesktopSettlementFailure(ref settlementFailure, failure);
        }

        if (!VulkanDesktopFramePolicy.IsAcquireFinalizationLegal(attempt.AcquireOwnership) ||
            !VulkanDesktopFramePolicy.IsUploadFinalizationLegal(attempt.UploadOwnership))
        {
            AddDesktopSettlementFailure(
                ref settlementFailure,
                new InvalidOperationException(
                    $"Desktop frame settlement left unresolved ownership. Acquire={attempt.AcquireOwnership} Upload={attempt.UploadOwnership}."));
        }
        bool ownershipSettled =
            VulkanDesktopFramePolicy.IsAcquireFinalizationLegal(attempt.AcquireOwnership) &&
            VulkanDesktopFramePolicy.IsUploadFinalizationLegal(attempt.UploadOwnership);
        EVulkanFrameOutcome outcome = settlementFailure is null
            ? ResolveDesktopFrameTelemetryOutcome(ref attempt)
            : EVulkanFrameOutcome.Failed;
        attempt.PublishTerminalResult(
            new VulkanDesktopFrameTerminalResult(
                outcome,
                attempt.Reason,
                ownershipSettled));

        return settlementFailure;
    }

    private static void AddDesktopSettlementFailure(
        ref Exception? settlementFailure,
        Exception failure)
    {
        settlementFailure = settlementFailure is null
            ? failure
            : new AggregateException(settlementFailure, failure);
    }

    internal void ResetResourceCatchUpProgress()
    {
        _resourceCatchUpStartedAt = 0;
        _resourceCatchUpBlockedFrames = 0;
    }

    internal (ulong BlockedFrames, TimeSpan Elapsed) RecordResourceCatchUpProgress(long timestamp)
    {
        if (_resourceCatchUpStartedAt == 0)
            _resourceCatchUpStartedAt = timestamp;

        return (++_resourceCatchUpBlockedFrames,
            Stopwatch.GetElapsedTime(_resourceCatchUpStartedAt, timestamp));
    }

    /// <summary>Executes one allocation-free desktop callback attempt.</summary>
    internal void Render(double delta)
    {
        _ = delta;
        if (IsQuiescing)
            return;

        if (!TryEnter(out DesktopFrameIdentity desktopFrameIdentity))
        {
            ReportReentrantDesktopFrame();
            return;
        }

        try
        {
            ExecuteDesktopFrameTransaction(in desktopFrameIdentity);
        }
        finally
        {
            Exit(in desktopFrameIdentity);
        }
    }

    private void ExecuteDesktopFrameTransaction(in DesktopFrameIdentity identity)
    {
        VulkanFrameAttempt attempt = new(in identity);
        try
        {
            attempt.Timing = _telemetry.BeginFrame(identity);
            RunDesktopFramePhases(ref attempt);
        }
        catch (Exception primaryFailure)
        {
            attempt.PrimaryFailure = primaryFailure;
            throw;
        }
        finally
        {
            SettleAndPublishDesktopFrame(ref attempt);
        }
    }

    private void RunDesktopFramePhases(ref VulkanFrameAttempt attempt)
    {
        if (!_deviceContext.StateMachine.IsOperational)
            throw CreateDeviceLostException("RenderWindow", Result.ErrorDeviceLost);

        _telemetry.PublishDescriptorTableGeneration(_resourceRuntime.DescriptorTableGeneration);
        _resourceRuntime.Descriptors.Heap.BeginFrame(attempt.FrameNumber);
        RecordDesktopFrameGap(ref attempt);

        if (!CompleteDesktopFramePhase(
                ref attempt,
                EVulkanFrameStage.FramePacing,
                RunDesktopFramePreflight(ref attempt)).ShouldContinue)
            return;

        if (!CompleteDesktopFramePhase(
                ref attempt,
                EVulkanFrameStage.CompletionMaintenance,
                PrepareDesktopFrameSlot(ref attempt)).ShouldContinue)
            return;

        if (!CompleteDesktopFramePhase(
                ref attempt,
                EVulkanFrameStage.OutputAcquire,
                AcquireDesktopSwapchainImageCore(ref attempt)).ShouldContinue)
            return;

        PrepareAcquiredDesktopImage(ref attempt);
        if (!RecordDesktopFrame(ref attempt).ShouldContinue ||
            !SubmitDesktopFrame(ref attempt).ShouldContinue)
            return;

        _ = CompleteDesktopFramePhase(
            ref attempt,
            EVulkanFrameStage.OutputComplete,
            PresentSubmittedDesktopFrame(ref attempt));
    }

    private void SettleAndPublishDesktopFrame(ref VulkanFrameAttempt attempt)
    {
        Exception? settlementFailure = SettleDesktopFrameAttempt(ref attempt);
        Exception? telemetryFailure = null;
        try
        {
            PublishDesktopFrameTelemetry(ref attempt);
        }
        catch (Exception failure)
        {
            telemetryFailure = failure;
            ReportDesktopFrameTelemetryFailure(failure);
        }

        if (attempt.PrimaryFailure is not null)
        {
            if (settlementFailure is not null)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Desktop frame settlement failed after {0}: {1}",
                    attempt.PrimaryFailure.GetType().Name,
                    settlementFailure.Message);
            }

            return;
        }

        if (settlementFailure is not null)
            ExceptionDispatchInfo.Capture(settlementFailure).Throw();
        else if (telemetryFailure is not null)
            ExceptionDispatchInfo.Capture(telemetryFailure).Throw();
    }
}
