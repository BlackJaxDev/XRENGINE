using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    private string FrameExecutionLabel
        => TargetExecutionMode switch
        {
            RenderExecutionMode.DesktopWsi => "DesktopWsi",
            RenderExecutionMode.Presentationless => "Presentationless",
            RenderExecutionMode.Component => "Component",
            RenderExecutionMode.HeadlessWsi => "HeadlessWsi",
            RenderExecutionMode.OpenXr => "OpenXr",
            _ => "Vulkan",
        };

    private string FrameSubmissionProfileName
        => TargetExecutionMode switch
        {
            RenderExecutionMode.DesktopWsi =>
                "Vulkan.FrameLifecycle.DesktopWsi.Submit",
            RenderExecutionMode.Presentationless =>
                "Vulkan.FrameLifecycle.Presentationless.Submit",
            RenderExecutionMode.Component =>
                "Vulkan.FrameLifecycle.Component.Submit",
            RenderExecutionMode.HeadlessWsi =>
                "Vulkan.FrameLifecycle.HeadlessWsi.Submit",
            RenderExecutionMode.OpenXr =>
                "Vulkan.FrameLifecycle.OpenXr.Submit",
            _ => "Vulkan.FrameLifecycle.Submit",
        };

    private string FrameSubmissionKind
        => TargetExecutionMode switch
        {
            RenderExecutionMode.Presentationless =>
                "PresentationlessFrame",
            RenderExecutionMode.Component => "ComponentFrame",
            RenderExecutionMode.HeadlessWsi => "HeadlessWsiFrame",
            RenderExecutionMode.OpenXr => "OpenXrFrame",
            _ => "VulkanFrame",
        };

    private string FrameSubmissionTraceKey
        => TargetExecutionMode switch
        {
            RenderExecutionMode.Presentationless =>
                "Vulkan.FrameTarget.Presentationless.Submit",
            RenderExecutionMode.Component =>
                "Vulkan.FrameTarget.Component.Submit",
            RenderExecutionMode.HeadlessWsi =>
                "Vulkan.FrameTarget.HeadlessWsi.Submit",
            RenderExecutionMode.OpenXr =>
                "Vulkan.FrameTarget.OpenXr.Submit",
            _ => "Vulkan.FrameTarget.Submit",
        };

    /// <summary>
    /// Executes an explicit target frame through the same allocation-free queue
    /// submission primitive used by the desktop production frame loop.
    /// </summary>
    internal unsafe void ExecuteExplicitTargetFrame(
        Action<Vk, CommandBuffer, VulkanRenderFrameTarget> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!TryEnterExplicitFrameExecution())
            throw new ObjectDisposedException(nameof(VulkanFrameLoop));

        try
        {
            ExecuteExplicitTargetFrameCore(record);
        }
        finally
        {
            ExitExplicitFrameExecution();
        }
    }

    private unsafe void ExecuteExplicitTargetFrameCore(
        Action<Vk, CommandBuffer, VulkanRenderFrameTarget> record)
    {
        bool captureAllocations = ExplicitTargetAllocationDiagnosticsEnabled;
        long allocationCheckpoint = captureAllocations
            ? GC.GetAllocatedBytesForCurrentThread()
            : 0L;
        long acquireAllocatedBytes = 0L;
        long beginAllocatedBytes = 0L;
        long beginTrackedCommandBufferAllocatedBytes = 0L;
        long beginFrameResourceTrackingAllocatedBytes = 0L;
        VulkanCommandBufferBeginAllocationCounters beginCommandBufferAllocationCounters = default;
        long recordAllocatedBytes = 0L;
        long endAllocatedBytes = 0L;
        long submitAllocatedBytes = 0L;
        long completeAllocatedBytes = 0L;
        IVulkanExplicitFrameTargetDriver target = RequireExplicitFrameTarget();
        VulkanFrameTargetLease lease = default;
        bool acquired = false;
        bool submitted = false;
        long frameStart = Stopwatch.GetTimestamp();
        ulong frameNumber = unchecked((ulong)OutputRuntime.NextExplicitTargetFrameNumber());
        VulkanFrameRootIdentity rootIdentity = new(
            frameNumber,
            frameNumber,
            -1,
            frameStart,
            new VulkanFrameOutputIdentity(-1, 0));
        VulkanFrameTrace frameTrace = _frameTelemetry.BeginFrame(rootIdentity);
        _resourceRuntime.BeginRetirementMeteringFrame(unchecked((long)frameNumber));
        EVulkanFrameOutcome frameOutcome = EVulkanFrameOutcome.Failed;

        try
        {
            long acquireStart = Stopwatch.GetTimestamp();
            lease = target.AcquireFrameTarget(
                out CommandBuffer commandBuffer);
            if (captureAllocations)
            {
                acquireAllocatedBytes =
                    GC.GetAllocatedBytesForCurrentThread() - allocationCheckpoint;
                allocationCheckpoint = GC.GetAllocatedBytesForCurrentThread();
            }
            acquired = true;
            if (!lease.IsValid)
            {
                throw new InvalidOperationException(
                    $"Vulkan target '{FrameExecutionLabel}' returned an invalid frame-target lease.");
            }
            frameTrace.SetOutputIdentity(
                unchecked((int)lease.Target.FrameSlotIndex),
                lease.Target.TargetGeneration);
            frameTrace.RecordStage(
                EVulkanFrameStage.OutputAcquire,
                Stopwatch.GetElapsedTime(acquireStart),
                EVulkanFrameIntervalClass.Driver,
                EVulkanFrameOutcome.Completed,
                EVulkanFrameWaitReason.Driver);

            long recordStart = Stopwatch.GetTimestamp();
            target.BeginFrameRecording(in lease, commandBuffer);
            if (captureAllocations)
            {
                beginAllocatedBytes =
                    GC.GetAllocatedBytesForCurrentThread() - allocationCheckpoint;
                allocationCheckpoint = GC.GetAllocatedBytesForCurrentThread();
                if (target is VulkanPresentationlessTargetDriver presentationlessTarget)
                {
                    beginTrackedCommandBufferAllocatedBytes =
                        presentationlessTarget.LastBeginTrackedCommandBufferAllocatedBytes;
                    beginFrameResourceTrackingAllocatedBytes =
                        presentationlessTarget.LastBeginFrameResourceTrackingAllocatedBytes;
                    beginCommandBufferAllocationCounters =
                        presentationlessTarget.LastBeginCommandBufferAllocationCounters;
                }
            }
            record(Api, commandBuffer, lease.Target);
            if (captureAllocations)
            {
                recordAllocatedBytes =
                    GC.GetAllocatedBytesForCurrentThread() - allocationCheckpoint;
                allocationCheckpoint = GC.GetAllocatedBytesForCurrentThread();
            }
            target.EndFrameRecording(in lease, commandBuffer);
            if (captureAllocations)
            {
                endAllocatedBytes =
                    GC.GetAllocatedBytesForCurrentThread() - allocationCheckpoint;
                allocationCheckpoint = GC.GetAllocatedBytesForCurrentThread();
            }
            frameTrace.RecordStage(
                EVulkanFrameStage.CommandRecord,
                Stopwatch.GetElapsedTime(recordStart),
                EVulkanFrameIntervalClass.Work,
                EVulkanFrameOutcome.Completed);

            CommandBuffer* commandBuffers = stackalloc CommandBuffer[1]
            {
                commandBuffer,
            };

            long submitStart = Stopwatch.GetTimestamp();
            Result result;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       FrameSubmissionProfileName))
            using (VulkanCpuStageScope cpuStage =
                   new(_frameTelemetry, EVulkanCpuStage.Submission))
            {
                VulkanSubmissionDiagnosticContext diagnosticContext =
                    CreateFrameTargetSubmissionDiagnosticContext(
                        in lease,
                        frameNumber,
                        commandBuffer,
                        FrameSubmissionKind);
                VulkanSubmissionReceipt receipt = SubmitFrameTargetLease(
                    in lease,
                    commandBuffers,
                    commandBufferCount: 1,
                    signalGraphicsTimeline: false,
                    minimumGraphicsTimelineSignalValue: 0,
                    out _,
                    in diagnosticContext,
                    caller: nameof(ExecuteExplicitTargetFrame));
                result = receipt.Result;
                if (result == Result.Success)
                {
                    // Queue acceptance transfers output ownership immediately. Publish it before
                    // telemetry, diagnostics, or profiling teardown can throw so catch settles
                    // accepted work correctly.
                    submitted = true;
                    target.NotifyFrameSubmitted(in lease);
                }
            }
            if (captureAllocations)
            {
                submitAllocatedBytes =
                    GC.GetAllocatedBytesForCurrentThread() - allocationCheckpoint;
                allocationCheckpoint = GC.GetAllocatedBytesForCurrentThread();
            }
            frameTrace.RecordStage(
                EVulkanFrameStage.QueueSubmit,
                Stopwatch.GetElapsedTime(submitStart),
                EVulkanFrameIntervalClass.Driver,
                result == Result.Success
                    ? EVulkanFrameOutcome.Completed
                    : EVulkanFrameOutcome.Failed,
                EVulkanFrameWaitReason.Driver);

            if (VulkanFrameDiagnosticsTraceEnabled)
            {
                TimeSpan elapsed =
                    Stopwatch.GetElapsedTime(submitStart);
                Debug.VulkanEvery(
                    FrameSubmissionTraceKey,
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Mode={0} TargetGeneration={1} FrameSlot={2} Image={3} SubmitMs={4:F3}",
                    FrameExecutionLabel,
                    lease.Target.TargetGeneration,
                    lease.Target.FrameSlotIndex,
                    lease.ImageIndex,
                    elapsed.TotalMilliseconds);
            }

            if (result != Result.Success)
            {
                if (result == Result.ErrorDeviceLost)
                    throw new InvalidOperationException(
                        $"{FrameExecutionLabel} QueueSubmit returned {result}.");

                throw new InvalidOperationException(
                    $"Vulkan {FrameExecutionLabel} queue submission failed ({result}).");
            }

            long completionStart = Stopwatch.GetTimestamp();
            target.CompleteFrameTarget(in lease);
            if (captureAllocations)
                completeAllocatedBytes =
                    GC.GetAllocatedBytesForCurrentThread() - allocationCheckpoint;
            frameTrace.RecordStage(
                EVulkanFrameStage.OutputComplete,
                Stopwatch.GetElapsedTime(completionStart),
                EVulkanFrameIntervalClass.Work,
                EVulkanFrameOutcome.Completed);
            frameOutcome = EVulkanFrameOutcome.Completed;
        }
        catch
        {
            if (acquired)
                target.AbortFrameTarget(in lease, submitted);
            throw;
        }
        finally
        {
            if (captureAllocations)
            {
                PublishExplicitTargetFrameAllocationCounters(
                    acquireAllocatedBytes,
                    beginAllocatedBytes,
                    beginTrackedCommandBufferAllocatedBytes,
                    beginFrameResourceTrackingAllocatedBytes,
                    beginCommandBufferAllocationCounters.BindStateInitialization,
                    beginCommandBufferAllocationCounters.TrackingInitialization,
                    beginCommandBufferAllocationCounters.NativeBegin,
                    recordAllocatedBytes,
                    endAllocatedBytes,
                    submitAllocatedBytes,
                    completeAllocatedBytes);
            }
            frameTrace.PublishAfterFrame(
                Stopwatch.GetElapsedTime(frameStart),
                frameOutcome);
        }
    }

    /// <summary>
    /// Builds binary and optional timeline semaphore arrays on the stack and
    /// submits one frame-target lease without managed allocation.
    /// </summary>
    private unsafe VulkanSubmissionReceipt SubmitFrameTargetLease(
        in VulkanFrameTargetLease lease,
        CommandBuffer* commandBuffers,
        uint commandBufferCount,
        bool signalGraphicsTimeline,
        ulong minimumGraphicsTimelineSignalValue,
        out ulong graphicsTimelineSignalValue,
        in VulkanSubmissionDiagnosticContext diagnosticContext,
        string caller)
    {
        Semaphore* waitSemaphores = stackalloc Semaphore[1];
        PipelineStageFlags* waitStages =
            stackalloc PipelineStageFlags[1];
        ulong* waitValues = stackalloc ulong[1];
        uint waitSemaphoreCount = 0;
        if (lease.SubmissionWaitSemaphore.Handle != 0)
        {
            waitSemaphores[0] = lease.SubmissionWaitSemaphore;
            waitStages[0] = lease.SubmissionWaitStage != 0
                ? lease.SubmissionWaitStage
                : PipelineStageFlags.ColorAttachmentOutputBit;
            waitValues[0] = 0;
            waitSemaphoreCount = 1;
        }

        Semaphore* signalSemaphores = stackalloc Semaphore[2];
        ulong* signalValues = stackalloc ulong[2];
        uint signalSemaphoreCount = 0;
        if (signalGraphicsTimeline)
        {
            signalSemaphores[signalSemaphoreCount] =
                _commandRuntime.Synchronization._graphicsTimelineSemaphore;
            // The tracked graphics-timeline gateway patches this value while
            // holding submission-order serialization.
            signalValues[signalSemaphoreCount] = 0UL;
            signalSemaphoreCount++;
        }
        if (lease.SubmissionSignalSemaphore.Handle != 0)
        {
            signalSemaphores[signalSemaphoreCount] =
                lease.SubmissionSignalSemaphore;
            signalValues[signalSemaphoreCount] = 0;
            signalSemaphoreCount++;
        }

        TimelineSemaphoreSubmitInfo timelineSubmitInfo = new()
        {
            SType = StructureType.TimelineSemaphoreSubmitInfo,
            WaitSemaphoreValueCount = waitSemaphoreCount,
            PWaitSemaphoreValues = waitSemaphoreCount > 0
                ? waitValues
                : null,
            SignalSemaphoreValueCount = signalSemaphoreCount,
            PSignalSemaphoreValues = signalSemaphoreCount > 0
                ? signalValues
                : null,
        };
        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            PNext = signalGraphicsTimeline
                ? &timelineSubmitInfo
                : null,
            WaitSemaphoreCount = waitSemaphoreCount,
            PWaitSemaphores = waitSemaphoreCount > 0
                ? waitSemaphores
                : null,
            PWaitDstStageMask = waitSemaphoreCount > 0
                ? waitStages
                : null,
            CommandBufferCount = commandBufferCount,
            PCommandBuffers = commandBuffers,
            SignalSemaphoreCount = signalSemaphoreCount,
            PSignalSemaphores = signalSemaphoreCount > 0
                ? signalSemaphores
                : null,
        };

        if (signalGraphicsTimeline)
        {
            return _commandRuntime.SubmitToGraphicsTimelineTrackedWithDisposition(
                _deviceContext.GraphicsQueue,
                ref submitInfo,
                lease.CompletionFence,
                _commandRuntime.Synchronization._graphicsTimelineSemaphore,
                minimumGraphicsTimelineSignalValue,
                in diagnosticContext,
                out graphicsTimelineSignalValue,
                out _,
                out _,
                caller);
        }

        graphicsTimelineSignalValue = 0UL;
        return _commandRuntime.SubmitToQueueTrackedWithDisposition(
            _deviceContext.GraphicsQueue,
            ref submitInfo,
            lease.CompletionFence,
            in diagnosticContext,
            out _,
            out _,
            caller);
    }

    private VulkanSubmissionDiagnosticContext CreateFrameTargetSubmissionDiagnosticContext(
        in VulkanFrameTargetLease lease,
        ulong frameNumber,
        CommandBuffer firstCommandBuffer,
        string submissionKind)
        => new()
        {
            SubmissionKind = submissionKind,
            FrameOpKind = "FrameTarget",
            OutputTargetName = FrameExecutionLabel,
            OutputWidth = lease.Target.Extent.Width,
            OutputHeight = lease.Target.Extent.Height,
            InternalWidth = lease.Target.Extent.Width,
            InternalHeight = lease.Target.Extent.Height,
            FrameId = frameNumber,
            FrameSlot = checked((int)lease.Target.FrameSlotIndex),
            SwapchainImageIndex = lease.ImageIndex,
            ResourceGeneration = lease.Target.TargetGeneration,
            CommandBufferCount = 1,
            FirstCommandBufferHandle = unchecked((ulong)firstCommandBuffer.Handle),
            FenceHandle = unchecked((ulong)lease.CompletionFence.Handle),
        };
}
