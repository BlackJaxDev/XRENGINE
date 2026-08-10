using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns desktop frame admission, attempt identity, frame-slot progression, and
/// the ordered composition of each desktop frame attempt.
/// </summary>
internal sealed unsafe partial class VulkanFrameLoop
{
    private const int FrameSlotCount = 2;
    private readonly VulkanDeviceContext _deviceContext;
    private readonly VulkanOutputRuntime _outputRuntime;
    private readonly VulkanFramePlanner _framePlanner;
    private readonly VulkanResourceRuntime _resourceRuntime;
    private readonly VulkanCommandRuntime _commandRuntime;
    private readonly VulkanFrameTelemetry _telemetry;
    private readonly VulkanTextureReadbackService _textureReadbackService;
    private VulkanTrackedCommandEncoder? _overlayCommandEncoder;
    private readonly VulkanImGuiOverlayCommandRecorder _imguiOverlayRecorder = new();
    private readonly VulkanDeviceLossCoordinator _deviceLossCoordinator;
    private readonly DesktopFrameActivityState _activity = new();
    private readonly object _retirementGate = new();
    private int _frameSlot;
    private ulong _acceptedAttemptCount;
    private long _lastObservedTickTimestamp;
    private long _resourceCatchUpStartedAt;
    private ulong _resourceCatchUpBlockedFrames;

    internal VulkanFrameLoop(
        VulkanDeviceContext deviceContext,
        VulkanOutputRuntime outputRuntime,
        VulkanFramePlanner framePlanner,
        VulkanResourceRuntime resourceRuntime,
        VulkanCommandRuntime commandRuntime,
        VulkanFrameTelemetry telemetry,
        VulkanTextureReadbackService textureReadbackService)
    {
        _deviceContext = deviceContext;
        _outputRuntime = outputRuntime;
        _framePlanner = framePlanner;
        _resourceRuntime = resourceRuntime;
        _commandRuntime = commandRuntime;
        _telemetry = telemetry;
        _textureReadbackService = textureReadbackService;
        _resourceRuntime.Samplers.PublishFrameSlot(CurrentFrameSlot);
        _resourceRuntime.Images.PublishFrameSlot(CurrentFrameSlot);
        _resourceRuntime.Buffers.PublishFrameSlot(CurrentFrameSlot);
        _resourceRuntime.Descriptors.PublishFrameSlot(CurrentFrameSlot);
        _resourceRuntime.PublishFramebufferRetirementFrameSlot(CurrentFrameSlot);
        _deviceLossCoordinator = new VulkanDeviceLossCoordinator(
            deviceContext,
            commandRuntime,
            resourceRuntime,
            outputRuntime,
            telemetry);
        _resourceRuntime.Queries.BindDeviceLossCoordinator(_deviceLossCoordinator);
    }

    internal ulong AcceptedAttemptCount => Volatile.Read(ref _acceptedAttemptCount);
    private Vk Api => _deviceContext.Api;
    private VulkanTrackedCommandEncoder OverlayCommandEncoder
        => _overlayCommandEncoder ??= new VulkanTrackedCommandEncoder(
            _deviceContext.Api,
            _deviceContext,
            _commandRuntime,
            _resourceRuntime,
            _telemetry);
    private VulkanOutputRuntime OutputRuntime => _outputRuntime;
    private VulkanResourceRuntime ResourceRuntime => _resourceRuntime;
    private VulkanFrameTelemetry _frameTelemetry => _telemetry;
    private ResourcePlannerRuntimeState PublishedResourcePlannerRuntimeState
        => _framePlanner
            .GetPublishedResourcePlannerGeneration<ResourcePlannerRuntimeGeneration>()
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
        => _outputRuntime.RequireDesktopWsiTarget();
    private VulkanMappedFrameArena? MappedFrameArena
        => _resourceRuntime.MappedFrameArena;
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
            ResolveDesktopFrameTelemetryOutcome(ref attempt));
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

        VulkanFrameAttempt frameAttempt = new(_telemetry, in desktopFrameIdentity);
        try
        {
            if (!_deviceContext.StateMachine.IsOperational)
                throw CreateDeviceLostException("RenderWindow", Result.ErrorDeviceLost);

            _resourceRuntime.Descriptors.Heap.BeginFrame(frameAttempt.FrameNumber);
            RecordDesktopFrameGap(ref frameAttempt);

            if (RunDesktopFramePreflight(ref frameAttempt) != EDesktopFrameFlow.Continue ||
                PrepareDesktopFrameSlot(ref frameAttempt) != EDesktopFrameFlow.Continue ||
                AcquireDesktopSwapchainImageCore(ref frameAttempt) != EDesktopFrameFlow.Continue)
                return;

            PrepareAcquiredDesktopImage(ref frameAttempt);
            if (RecordDesktopFrame(ref frameAttempt) != EDesktopFrameFlow.Continue ||
                SubmitDesktopFrame(ref frameAttempt) != EDesktopFrameFlow.Continue)
                return;

            _ = PresentSubmittedDesktopFrame(ref frameAttempt);
        }
        catch (Exception primaryFailure)
        {
            frameAttempt.PrimaryFailure = primaryFailure;
            SettleDesktopAcquireAfterUnexpectedFailure(ref frameAttempt, primaryFailure);
            throw;
        }
        finally
        {
            try
            {
                PublishDesktopFrameTelemetry(ref frameAttempt);
            }
            catch (Exception telemetryFailure)
            {
                if (frameAttempt.PrimaryFailure is null)
                    throw;

                ReportDesktopFrameTelemetryFailure(telemetryFailure);
            }
            finally
            {
                Exit(in desktopFrameIdentity);
            }
        }
    }
}
