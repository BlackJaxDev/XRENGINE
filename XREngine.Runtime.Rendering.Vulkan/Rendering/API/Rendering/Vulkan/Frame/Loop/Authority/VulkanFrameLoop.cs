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
internal sealed unsafe partial class VulkanFrameLoop
{
    private const int FrameSlotCount = 2;
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
    internal VulkanMeshOperationRequestQueue MeshOperationRequests { get; } = new();
    private VulkanImGuiBackend? _imguiBackend;
    private readonly VulkanImGuiOverlayCommandRecorder _imguiOverlayRecorder = new();
    private readonly DesktopFrameActivityState _activity = new();
    private readonly object _retirementGate = new();
    private int _frameSlot;
    private ulong _acceptedAttemptCount;
    private long _lastObservedTickTimestamp;
    private long _resourceCatchUpStartedAt;
    private ulong _resourceCatchUpBlockedFrames;

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
        => PublishedResourcePlannerRuntimeState.ResourcePlannerSignature;
    private ulong ActiveResourceAllocationSignature
        => PublishedResourcePlannerRuntimeState.ResourceAllocationSignature;
    private FrameOpContext? ActiveLastActiveFrameOpContext
    {
        get => PublishedResourcePlannerRuntimeState.LastActiveFrameOpContext;
        set
        {
            ResourcePlannerRuntimeState state = PublishedResourcePlannerRuntimeState;
            state.LastActiveFrameOpContext = value;
            _framePlanner.PublishResourcePlannerGeneration(
                new ResourcePlannerRuntimeGeneration(state));
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
    internal long LastObservedTickTimestamp => Volatile.Read(ref _lastObservedTickTimestamp);
    internal bool HasObservedTick => LastObservedTickTimestamp != 0;

    internal DesktopFrameActivitySnapshot CaptureActivity()
        => _activity.Capture();

    internal bool TryEnter(out DesktopFrameIdentity identity)
    {
        lock (_retirementGate)
        {
            int frameSlot = CurrentFrameSlot;
            ulong frameNumber = checked(AcceptedAttemptCount + 1UL);
            if (!_activity.TryEnter(frameNumber, frameSlot, out long activityPublicationToken))
            {
                identity = default;
                return false;
            }

            Volatile.Write(ref _acceptedAttemptCount, frameNumber);
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
            _activity.TryExit(identity.ActivityPublicationToken);
    }

    internal void AdvanceFrameSlot(int completedFrameSlot)
    {
        int nextFrameSlot = (completedFrameSlot + 1) % FrameSlotCount;
        Volatile.Write(ref _frameSlot, nextFrameSlot);
        _resourceRuntime.Samplers.PublishFrameSlot(nextFrameSlot);
        _resourceRuntime.Images.PublishFrameSlot(nextFrameSlot);
        _resourceRuntime.Buffers.PublishFrameSlot(nextFrameSlot);
        _resourceRuntime.Descriptors.PublishFrameSlot(nextFrameSlot);
        _resourceRuntime.PublishFramebufferRetirementFrameSlot(nextFrameSlot);
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
