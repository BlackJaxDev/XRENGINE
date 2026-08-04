using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private long _explicitTargetFrameNumber;

    private string FrameExecutionLabel
        => ExecutionMode switch
        {
            RenderExecutionMode.DesktopWsi => "DesktopWsi",
            RenderExecutionMode.Presentationless => "Presentationless",
            RenderExecutionMode.Component => "Component",
            RenderExecutionMode.HeadlessWsi => "HeadlessWsi",
            RenderExecutionMode.OpenXr => "OpenXr",
            _ => "Vulkan",
        };

    private string FrameSubmissionProfileName
        => ExecutionMode switch
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
        => ExecutionMode switch
        {
            RenderExecutionMode.Presentationless =>
                "PresentationlessFrame",
            RenderExecutionMode.Component => "ComponentFrame",
            RenderExecutionMode.HeadlessWsi => "HeadlessWsiFrame",
            RenderExecutionMode.OpenXr => "OpenXrFrame",
            _ => "VulkanFrame",
        };

    private string FrameSubmissionTraceKey
        => ExecutionMode switch
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
    private void ExecuteExplicitTargetFrame(
        Action<Vk, CommandBuffer, VulkanRenderFrameTarget> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        IVulkanExplicitFrameTargetDriver target =
            RequireExplicitFrameTarget();
        VulkanFrameTargetLease lease = default;
        bool acquired = false;
        bool submitted = false;

        try
        {
            lease = target.AcquireFrameTarget(
                out CommandBuffer commandBuffer);
            acquired = true;
            if (!lease.IsValid)
            {
                throw new InvalidOperationException(
                    $"Vulkan target '{FrameExecutionLabel}' returned an invalid frame-target lease.");
            }

            record(VulkanApi, commandBuffer, lease.Target);
            target.EndFrameRecording(in lease, commandBuffer);

            ulong frameNumber = unchecked((ulong)Interlocked.Increment(
                ref _explicitTargetFrameNumber));
            VulkanSubmissionDiagnosticContext diagnosticContext =
                CreateFrameTargetSubmissionDiagnosticContext(
                    FrameSubmissionKind,
                    FrameExecutionLabel,
                    in lease,
                    frameNumber,
                    signalTimelineValue: 0);
            CommandBuffer* commandBuffers = stackalloc CommandBuffer[1]
            {
                commandBuffer,
            };

            long submitStart = Stopwatch.GetTimestamp();
            Result result;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       FrameSubmissionProfileName))
            using (VulkanCpuStageScope cpuStage =
                   new(EVulkanCpuStage.Submission))
            {
                result = SubmitFrameTargetLease(
                    in lease,
                    commandBuffers,
                    commandBufferCount: 1,
                    signalGraphicsTimeline: false,
                    graphicsTimelineSignalValue: 0,
                    diagnosticContext,
                    caller: nameof(SubmitExplicitTargetFrame));
            }

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
                    throw CreateDeviceLostException(
                        $"{FrameExecutionLabel} QueueSubmit",
                        result);

                throw new InvalidOperationException(
                    $"Vulkan {FrameExecutionLabel} queue submission failed ({result}).");
            }

            submitted = true;
            target.NotifyFrameSubmitted(in lease);
            target.CompleteFrameTarget(in lease);
        }
        catch
        {
            if (acquired)
                target.AbortFrameTarget(in lease, submitted);
            throw;
        }
    }

    /// <summary>
    /// Builds binary and optional timeline semaphore arrays on the stack and
    /// submits one frame-target lease without managed allocation.
    /// </summary>
    private Result SubmitFrameTargetLease(
        in VulkanFrameTargetLease lease,
        CommandBuffer* commandBuffers,
        uint commandBufferCount,
        bool signalGraphicsTimeline,
        ulong graphicsTimelineSignalValue,
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
                _graphicsTimelineSemaphore;
            signalValues[signalSemaphoreCount] =
                graphicsTimelineSignalValue;
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

        lock (_oneTimeSubmitLock)
        {
            return SubmitToQueueTracked(
                graphicsQueue,
                ref submitInfo,
                lease.CompletionFence,
                diagnosticContext,
                caller);
        }
    }
}
