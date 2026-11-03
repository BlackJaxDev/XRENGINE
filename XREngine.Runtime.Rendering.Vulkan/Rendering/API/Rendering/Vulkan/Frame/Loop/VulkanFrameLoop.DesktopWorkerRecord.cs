using Silk.NET.Vulkan;
using XREngine.Execution;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    /// <summary>The background render lane that desktop worker recording reserves.</summary>
    private const int DesktopRecordLaneId = 2;

    private readonly VulkanDesktopRecordWorkExecutor _desktopRecordExecutor = new();
    private int _lastReportedDesktopExecutionState = -1;
    private int _reportedWorkerFailureCount;
    private long _workerTimingWindowStart;
    private int _workerTimingCount;
    private long _workerTimingStartDelaySum;
    private long _workerTimingRecordSum;
    private long _workerTimingRecordMax;
    private long _workerTimingJoinSum;

    /// <summary>
    /// Records the frozen desktop primary with the selected execution mode. Inline
    /// records on the render thread. WorkerRecord records on reserved lane 2 through
    /// an asynchronous render-work batch. A missing lane or capacity fails loudly; it
    /// never records inline instead.
    /// </summary>
    private VulkanPrimaryCommandRecordingResult RecordDesktopPrimaryForExecutionMode(
        in VulkanPreparedPrimaryCommandInput input)
    {
        IRuntimeRenderPresentationServices presentation = RuntimeRenderingHostServices.Presentation;
        bool inVr = presentation.IsInVR;
        EVrDesktopFrameExecutionMode mode = presentation.VrDesktopFrameExecutionMode;
        ReportDesktopExecutionState(inVr, mode);
        if (!inVr || mode != EVrDesktopFrameExecutionMode.WorkerRecord)
            return _commandRuntime.RecordPrimary(in input);

        try
        {
            VulkanPrimaryCommandRecordingResult result = RecordDesktopPrimaryOnWorkerLane(in input);
            if (!result.Succeeded)
                ReportWorkerResult(in result);
            return result;
        }
        catch (Exception ex) when (ReportWorkerFailure(ex))
        {
            throw;
        }
    }

    /// <summary>
    /// Writes the first unsuccessful worker recording results to the auxiliary log.
    /// A deferred or replanned result is not an exception, so the failure report
    /// does not show it.
    /// </summary>
    private void ReportWorkerResult(in VulkanPrimaryCommandRecordingResult result)
    {
        if (_reportedWorkerFailureCount >= 10)
            return;

        _reportedWorkerFailureCount++;
        Debug.WriteAuxiliaryLog(
            "desktop-execution-mode.log",
            $"Worker recording result: disposition={result.Disposition} failure={result.FailureKind} reason={result.Reason ?? "<none>"}");
    }

    /// <summary>
    /// Writes the first worker recording failures to the auxiliary log, because a
    /// Release build compiles out the general log. Always returns false so the
    /// exception filter does not catch the exception.
    /// </summary>
    private bool ReportWorkerFailure(Exception ex)
    {
        if (_reportedWorkerFailureCount >= 5)
            return false;

        _reportedWorkerFailureCount++;
        Debug.WriteAuxiliaryLog("desktop-execution-mode.log", $"Worker recording failed: {ex}");
        return false;
    }

    /// <summary>
    /// Records the desktop primary on the reserved worker lane. Until the desktop
    /// frame transaction is split, the join follows the submit at once, so the
    /// render thread still waits for the recording; the split moves the join to
    /// the next desktop opportunity.
    /// </summary>
    private VulkanPrimaryCommandRecordingResult RecordDesktopPrimaryOnWorkerLane(
        in VulkanPreparedPrimaryCommandInput input)
    {
        RenderWorkDomain domain = _commandRuntime.RenderDomain;
        if (domain.LogicalLaneCount <= DesktopRecordLaneId)
        {
            throw new InvalidOperationException(
                $"Desktop worker recording needs render lane {DesktopRecordLaneId}, but the render domain has " +
                $"{domain.LogicalLaneCount} lanes. Select Inline or raise the render worker count.");
        }

        RenderWorkAdmissionResult admission = domain.TryRentAsynchronousBatch(
            _desktopRecordExecutor,
            itemCount: 1,
            dependencyCount: 0,
            out RenderWorkBatchLease lease);
        if (!admission.IsAdmitted)
            throw new InvalidOperationException($"Desktop worker recording was not admitted: {admission.Reason}.");

        try
        {
            admission = domain.TryReserveAsynchronousLanes(lease, 1UL << DesktopRecordLaneId);
            if (!admission.IsAdmitted)
            {
                throw new InvalidOperationException(
                    $"Desktop worker recording could not reserve lane {DesktopRecordLaneId}: {admission.Reason}.");
            }

            _desktopRecordExecutor.Prepare(
                _commandRuntime,
                CaptureDesktopRecordingPlannerGeneration(),
                in input,
                DesktopRecordLaneId);
            lease.SetItem(
                0,
                new RenderWorkItem(
                    VulkanDesktopRecordWorkExecutor.DesktopPrimaryOperationKind,
                    SourceStart: 0,
                    SourceCount: 1,
                    PreferredLane: DesktopRecordLaneId,
                    EstimatedCost: 64));
            long submitTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            FramePacingTimeline.Mark(EFramePacingMark.DesktopWorkerSubmit);
            domain.SubmitAsynchronous(
                lease,
                _desktopRecordExecutor,
                _commandRuntime.ResolveRenderLaneFrameSlot(input.FrameDataSlotIndex));

            RenderWorkBatchResult batchResult = domain.JoinAsynchronous(lease);
            FramePacingTimeline.Mark(EFramePacingMark.DesktopWorkerJoined);
            RecordWorkerTiming(submitTicks, System.Diagnostics.Stopwatch.GetTimestamp());
            if (!batchResult.Succeeded)
            {
                _desktopRecordExecutor.Clear();
                throw new InvalidOperationException(
                    $"Desktop worker recording batch ended as {batchResult.Status}.",
                    batchResult.Exception);
            }

            return _desktopRecordExecutor.TakeResult();
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// Accumulates worker start delay, record time and join time, and writes the
    /// averages to the auxiliary log every two seconds.
    /// </summary>
    private void RecordWorkerTiming(long submitTicks, long joinedTicks)
    {
        (long start, long end) = _desktopRecordExecutor.LastExecuteTicks;
        _workerTimingCount++;
        _workerTimingStartDelaySum += Math.Max(0L, start - submitTicks);
        long record = Math.Max(0L, end - start);
        _workerTimingRecordSum += record;
        _workerTimingRecordMax = Math.Max(_workerTimingRecordMax, record);
        _workerTimingJoinSum += Math.Max(0L, joinedTicks - submitTicks);
        if (_workerTimingWindowStart == 0L)
            _workerTimingWindowStart = joinedTicks;
        if (joinedTicks - _workerTimingWindowStart < 2L * System.Diagnostics.Stopwatch.Frequency)
            return;

        double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        int count = Math.Max(1, _workerTimingCount);
        Debug.WriteAuxiliaryLog(
            "desktop-execution-mode.log",
            $"Worker timing: frames={_workerTimingCount} startDelay={_workerTimingStartDelaySum * toMs / count:F2}ms " +
            $"record={_workerTimingRecordSum * toMs / count:F2}ms recordMax={_workerTimingRecordMax * toMs:F2}ms " +
            $"submitToJoin={_workerTimingJoinSum * toMs / count:F2}ms");
        _workerTimingWindowStart = joinedTicks;
        _workerTimingCount = 0;
        _workerTimingStartDelaySum = 0L;
        _workerTimingRecordSum = 0L;
        _workerTimingRecordMax = 0L;
        _workerTimingJoinSum = 0L;
    }

    /// <summary>
    /// Returns the offscreen color targets when this desktop frame records on the
    /// worker lane, or null for inline recording. Worker recording always records
    /// into the offscreen target. It needs dynamic rendering and no Streamline frame
    /// generation; without them it fails loudly instead of recording inline.
    /// </summary>
    private VulkanDesktopOffscreenColorResources? ResolveDesktopOffscreenColorTarget()
    {
        IRuntimeRenderPresentationServices presentation = RuntimeRenderingHostServices.Presentation;
        if (!presentation.IsInVR ||
            presentation.VrDesktopFrameExecutionMode != EVrDesktopFrameExecutionMode.WorkerRecord)
        {
            return null;
        }

        if (_commandRuntime.RenderDomain.LogicalLaneCount <= DesktopRecordLaneId)
        {
            throw new InvalidOperationException(
                $"Desktop worker recording needs render lane {DesktopRecordLaneId}, but the render domain has " +
                $"{_commandRuntime.RenderDomain.LogicalLaneCount} lanes. Select Inline or raise the render worker count.");
        }
        if (!UseDynamicRenderingRenderTargets)
            throw new InvalidOperationException("Desktop worker recording needs dynamic rendering. Select Inline.");
        if (OutputRuntime.Desktop.StreamlineFrameGenerationActive)
            throw new InvalidOperationException("Desktop worker recording does not support Streamline frame generation. Select Inline.");

        return EnsureDesktopOffscreenColorResources();
    }

    /// <summary>
    /// Returns the desktop worker primary owner for <paramref name="frameSlot"/>. The
    /// owner comes from the retained pool of lane 2's arena for that slot, the arena
    /// the worker records with, and its artifact index is outside the swapchain range.
    /// </summary>
    private PrimaryCommandArtifactOwner GetOrCreateDesktopWorkerPrimaryOwner(
        uint frameSlot,
        out uint artifactIndex)
    {
        artifactIndex = VulkanCommandRuntime.DesktopWorkerArtifactIndexBase + frameSlot;
        int laneFrameSlot = _commandRuntime.ResolveRenderLaneFrameSlot(frameSlot);
        VulkanLaneCommandFamilyArena laneArena =
            _commandRuntime.GetRenderLaneAttachment(DesktopRecordLaneId, laneFrameSlot).Graphics;
        FrameOpSignatureHasher keyHash = new();
        keyHash.Add(0x4445534B544F5057UL);
        keyHash.Add((ulong)DesktopRecordLaneId);
        keyHash.Add((ulong)laneFrameSlot);
        try
        {
            return _commandRuntime.GetOrCreateDesktopWorkerPrimaryCommandBufferOwner(
                keyHash.ToHash(),
                artifactIndex,
                laneArena);
        }
        catch (Exception ex) when (ReportWorkerFailure(ex))
        {
            throw;
        }
    }

    /// <summary>
    /// Records the copy from the offscreen color target into the acquired image.
    /// The acquired image then has the layout that the scene would have left:
    /// color attachment when the ImGui overlay follows, otherwise present.
    /// </summary>
    private void RecordDesktopOffscreenCopy(ref VulkanFrameAttempt attempt)
    {
        CommandBuffer[]? copies = _commandRuntime.CommandBuffers.DesktopOffscreenCopies;
        Image[]? swapchainImages = OutputRuntime.Desktop.Images;
        if (copies is null || attempt.ImageIndex >= copies.Length ||
            swapchainImages is null || attempt.ImageIndex >= swapchainImages.Length)
        {
            throw new InvalidOperationException(
                $"The desktop offscreen copy has no command buffer or image for image {attempt.ImageIndex}.");
        }
        if (attempt.SceneCommandBuffer.Handle == 0)
            throw new InvalidOperationException("The desktop offscreen copy needs the recorded scene primary.");

        ImageLayout finalLayout = attempt.PreserveSwapchainForImGuiOverlay
            ? ImageLayout.ColorAttachmentOptimal
            : ImageLayout.PresentSrcKhr;
        VulkanDesktopOffscreenCopyInput input = new(
            copies[attempt.ImageIndex],
            attempt.SceneCommandBuffer,
            attempt.DesktopOffscreenImage,
            attempt.SwapchainLayoutAfterScene,
            swapchainImages[attempt.ImageIndex],
            OutputRuntime.Desktop.Extent,
            finalLayout);
        VulkanDesktopOffscreenCopyRecorder.Record(
            new VulkanTrackedCommandEncoder(_commandRuntime),
            _telemetry,
            in input);
        attempt.DesktopOffscreenCopyCommandBuffer = input.CopyCommandBuffer;
        attempt.HasDesktopOffscreenCopyCommandBuffer = true;
        attempt.SwapchainLayoutAfterScene = finalLayout;
    }

    /// <summary>
    /// The command buffer that last touched the acquired image before the overlays:
    /// the offscreen copy when the scene records offscreen, otherwise the scene.
    /// </summary>
    private static CommandBuffer ResolveDesktopSwapchainPredecessor(in VulkanFrameAttempt attempt)
        => attempt.HasDesktopOffscreenCopyCommandBuffer
            ? attempt.DesktopOffscreenCopyCommandBuffer
            : attempt.SceneCommandBuffer;

    /// <summary>
    /// Writes the effective desktop execution mode to the auxiliary log when it
    /// changes, so a run can confirm which recording path it used.
    /// </summary>
    private void ReportDesktopExecutionState(bool inVr, EVrDesktopFrameExecutionMode mode)
    {
        int state = ((int)mode << 1) | (inVr ? 1 : 0);
        if (state == _lastReportedDesktopExecutionState)
            return;

        _lastReportedDesktopExecutionState = state;
        Debug.WriteAuxiliaryLog(
            "desktop-execution-mode.log",
            $"Desktop primary recording: mode={mode} inVr={inVr} recordsOnWorker={inVr && mode == EVrDesktopFrameExecutionMode.WorkerRecord}");
    }

    /// <summary>
    /// Returns the planner generation that the render thread would record with: the
    /// thread's scoped generation when this runtime owns it, otherwise the published one.
    /// </summary>
    private ResourcePlannerRuntimeGeneration CaptureDesktopRecordingPlannerGeneration()
    {
        VulkanCommandThreadContext threadContext = _commandRuntime.ThreadWorkspace.Current;
        return ReferenceEquals(threadContext.ResourcePlannerRuntimeStateOwner, _commandRuntime) &&
            threadContext.ResourcePlannerRuntimeGeneration is { } scopedGeneration
                ? scopedGeneration
                : _framePlanner.GetPublishedResourcePlannerGeneration();
    }
}
