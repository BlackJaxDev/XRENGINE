using System;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanFrameLoop
    {
        internal EDesktopFrameFlow PrepareDesktopFrameSlot(ref VulkanFrameAttempt attempt)
        {
            TimeSpan slotWaitElapsed = TimeSpan.Zero;
            ulong slotWaitValue;
            bool xrOwnsFrameDeadline =
                RuntimeRenderingHostServices.Presentation.IsOpenXRActive;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.WaitFrameSlot"))
            {
                slotWaitValue =
                    _commandRuntime.Synchronization._frameSlotTimelineValues![attempt.FrameSlot];
                bool slotWasPreWaited = slotWaitValue != 0UL &&
                    Volatile.Read(
                        ref _preWaitedFrameSlotTimelineValues[attempt.FrameSlot]) ==
                    slotWaitValue;
                bool deadlineBoundDesktop = attempt.InteractiveResize || xrOwnsFrameDeadline;
                bool frameSlotReady = slotWasPreWaited ||
                    HasTimelineValueCompleted(
                        _commandRuntime.Synchronization._graphicsTimelineSemaphore,
                        slotWaitValue);
                bool imageSlotsReady = !xrOwnsFrameDeadline ||
                    AreDesktopImageTimelinesCompleted();
                if (deadlineBoundDesktop && (!frameSlotReady || !imageSlotsReady))
                {
                    DrainSkippedResizeFrameOps(
                        deadlineBoundDesktop && xrOwnsFrameDeadline
                            ? "XR-owned desktop frame slot or swapchain image is still busy"
                            : "Interactive resize frame slot is still busy",
                        preserveTextureUploads: attempt.InteractiveResize);
                    if (attempt.InteractiveResize)
                        MarkSkippedResizeFrameObserved(attempt.StartTimestamp);
                    RuntimeRenderingHostServices.Presentation.RecordRenderFrameOutputWork(
                        new FrameOutputWorkTelemetry(GpuBudgetDeferrals: 1));
                    attempt.Stop(EDesktopFrameReason.FrameSlotBusy);
                    return EDesktopFrameFlow.Stop;
                }

                if (!slotWasPreWaited)
                {
                    long waitStartTimestamp = Stopwatch.GetTimestamp();
                    WaitForTimelineValue(
                        _commandRuntime.Synchronization._graphicsTimelineSemaphore,
                        slotWaitValue);
                    slotWaitElapsed = Stopwatch.GetElapsedTime(waitStartTimestamp);
                }
                Volatile.Write(
                    ref _preWaitedFrameSlotTimelineValues[attempt.FrameSlot],
                    0UL);
            }

            ResourceRuntime.ResidentTemplateFrameSlotLifetimes.ReleaseFrameSlot(
                attempt.FrameSlot);

            attempt.Timing.WaitFrameSlot += slotWaitElapsed;
            attempt.Timing.WaitCurrentFrameSlot += slotWaitElapsed;
            attempt.Timing.RecordCausalWait(new VulkanFrameCausalWait(
                EVulkanFrameWaitReason.FrameSlot,
                slotWaitElapsed,
                attempt.FrameNumber,
                attempt.FrameSlot,
                ImageIndex: -1,
                SemaphoreTargetValue: slotWaitValue,
                SemaphoreCompletedValue: slotWaitValue,
                QueueFamily: _deviceContext.QueueFamilies.GraphicsFamilyIndex ?? 0U,
                PendingCommandCount: 0,
                ConcurrentWorkerActivity: Volatile.Read(
                    ref _commandRuntime.Workers.ActiveWorkerCount),
                Stage: EVulkanFrameStage.CompletionMaintenance));

            if (FrameDataArena is { } frameDataArena &&
                !frameDataArena.TryResetFrameSlot(
                    checked((uint)attempt.FrameSlot),
                    frameDataArena.Generation,
                    submissionCompletionProven: slotWaitValue != 0))
            {
                throw new InvalidOperationException(
                    $"Vulkan frame-data arena slot {attempt.FrameSlot} could not be reopened after timeline completion {slotWaitValue}.");
            }

            long stageStartTimestamp = Stopwatch.GetTimestamp();
            if (attempt.InteractiveResize || xrOwnsFrameDeadline)
            {
                // The modal callback must remain bounded. Completed retirement
                // work is left for the next ordinary frame instead of turning a
                // repaint callback into an unbounded destruction drain.
                RuntimeRenderingHostServices.Presentation.RecordRenderFrameOutputWork(
                    new FrameOutputWorkTelemetry(PlannerEvictionDeferrals: 1));
            }
            else
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.DrainRetiredResources"))
            {
                _commandRuntime.DrainInvalidatedCommandBufferRecordings(
                    Api, ResourceRuntime);
                _commandRuntime.DrainRetiredSynchronousSubmissions();
                DrainRetiredDesktopSwapchainGenerations();
                _commandRuntime.DrainRetiredCommandBuffers(
                    Api,
                    _deviceContext.Device,
                    ResourceRuntime,
                    attempt.FrameSlot);
                _commandRuntime.DrainRetiredCommandPools(
                    Api,
                    _deviceContext.Device,
                    ResourceRuntime,
                    attempt.FrameSlot);
                ResourceRuntime.DrainRetiredDescriptorSets(
                    Api, _deviceContext.Device, attempt.FrameSlot);
                ResourceRuntime.DrainRetiredDescriptorPools(
                    Api, _deviceContext.Device, attempt.FrameSlot);
                ResourceRuntime.DrainRetiredPipelines(
                    Api, _deviceContext.Device, attempt.FrameSlot);
                ResourceRuntime.DrainRetiredPipelineLayouts(
                    Api, _deviceContext.Device, attempt.FrameSlot);
                ResourceRuntime.DrainRetiredDescriptorSetLayouts(
                    Api, _deviceContext.Device, attempt.FrameSlot);
                ResourceRuntime.DrainRetiredQueryPools(
                    Api, _deviceContext.Device, attempt.FrameSlot);
                ResourceRuntime.DrainRetiredBufferViews(
                    Api, _deviceContext.Device, attempt.FrameSlot);
                int pooledBuffers = ResourceRuntime.DrainRetiredBuffers(
                    Api,
                    _deviceContext.Device,
                    _frameTelemetry,
                    attempt.FrameSlot);
                if (pooledBuffers != 0)
                    ResourceRuntime.Allocations.Staging.Trim(
                        ResourceRuntime.BackendObjectContext ?? throw new InvalidOperationException(
                            "The Vulkan backend object context is not initialized."));
                ResourceRuntime.DrainRetiredFramebuffers(
                    Api, _deviceContext.Device, attempt.FrameSlot);
                ResourceRuntime.DrainRetiredImages(
                    Api, _deviceContext.Device, attempt.FrameSlot);
                ResourceRuntime.Uploads.DrainCompletedRecordedTextureUploadPublications(
                    Api,
                    _deviceContext,
                    _commandRuntime,
                    ResourceRuntime,
                    IsDeviceLost);
            }

            attempt.Timing.DrainRetiredResources +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);

            if (VulkanFrameDiagnosticsTraceEnabled)
            {
                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.Sizes",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Frame={0} WindowFB={1}x{2} Swapchain={3}x{4}",
                    attempt.FrameNumber,
                    attempt.LiveFramebufferWidth,
                    attempt.LiveFramebufferHeight,
                    OutputRuntime.Desktop.Extent.Width,
                    OutputRuntime.Desktop.Extent.Height);
            }

            attempt.AdvanceTo(EDesktopFramePhase.SlotReady);
            return EDesktopFrameFlow.Continue;
        }

        /// <summary>
        /// Pays the next slot's unavoidable reuse wait after the current submit
        /// but before releasing visibility collection. This makes the slot-ready
        /// publication truthful while presentation and non-render gameplay work
        /// can still overlap after the boundary.
        /// </summary>
        private void WaitForNextDesktopFrameSlotBeforeCollect(
            ref VulkanFrameAttempt attempt)
        {
            if (attempt.InteractiveResize ||
                RuntimeRenderingHostServices.Presentation.IsOpenXRActive ||
                FrameSlotCount <= 1)
            {
                return;
            }

            int nextFrameSlot = (attempt.FrameSlot + 1) % FrameSlotCount;
            ulong targetValue = _commandRuntime.Synchronization
                ._frameSlotTimelineValues![nextFrameSlot];
            if (targetValue == 0UL)
            {
                Volatile.Write(
                    ref _preWaitedFrameSlotTimelineValues[nextFrameSlot],
                    0UL);
                return;
            }

            long waitStart = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.PreCollectNextSlotWait"))
            {
                WaitForTimelineValue(
                    _commandRuntime.Synchronization._graphicsTimelineSemaphore,
                    targetValue);
            }
            TimeSpan elapsed = Stopwatch.GetElapsedTime(waitStart);
            Volatile.Write(
                ref _preWaitedFrameSlotTimelineValues[nextFrameSlot],
                targetValue);
            attempt.Timing.WaitFrameSlot += elapsed;
            attempt.Timing.WaitNextFrameSlotBeforeCollect += elapsed;
            attempt.Timing.RecordCausalWait(new VulkanFrameCausalWait(
                EVulkanFrameWaitReason.FrameSlot,
                elapsed,
                attempt.FrameNumber,
                nextFrameSlot,
                ImageIndex: -1,
                SemaphoreTargetValue: targetValue,
                SemaphoreCompletedValue: targetValue,
                QueueFamily: _deviceContext.QueueFamilies.GraphicsFamilyIndex ?? 0U,
                PendingCommandCount: 0,
                ConcurrentWorkerActivity: Volatile.Read(
                    ref _commandRuntime.Workers.ActiveWorkerCount),
                Stage: EVulkanFrameStage.QueueSubmit));
        }

        private bool AreDesktopImageTimelinesCompleted()
        {
            ulong[]? imageTimelineValues = OutputRuntime.Desktop.ImageTimelineValues;
            if (imageTimelineValues is null)
                return true;

            for (int index = 0; index < imageTimelineValues.Length; index++)
            {
                if (!HasTimelineValueCompleted(
                        _commandRuntime.Synchronization._graphicsTimelineSemaphore,
                        imageTimelineValues[index]))
                {
                    return false;
                }
            }

            return true;
        }

        internal void PrepareAcquiredDesktopImage(ref VulkanFrameAttempt attempt)
        {
            ThrowIfDesktopFrameFaultInjected(
                EVulkanDesktopFrameFaultPoint.ImagePreparation);
            long stageStartTimestamp = Stopwatch.GetTimestamp();
            ulong imageCompletionValue = 0;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.WaitSwapchainImage"))
            {
                if (OutputRuntime.Desktop.ImageTimelineValues is not null &&
                    attempt.ImageIndex < OutputRuntime.Desktop.ImageTimelineValues.Length)
                {
                    imageCompletionValue =
                        OutputRuntime.Desktop.ImageTimelineValues[attempt.ImageIndex];
                    if (!RuntimeRenderingHostServices.Presentation.IsOpenXRActive)
                    {
                        WaitForTimelineValue(
                            _commandRuntime.Synchronization._graphicsTimelineSemaphore,
                            imageCompletionValue);
                    }
                }
            }

            TimeSpan imageWaitElapsed = Stopwatch.GetElapsedTime(stageStartTimestamp);
            attempt.Timing.WaitSwapchainImage += imageWaitElapsed;
            attempt.Timing.RecordCausalWait(new VulkanFrameCausalWait(
                EVulkanFrameWaitReason.OutputImage,
                imageWaitElapsed,
                attempt.FrameNumber,
                attempt.FrameSlot,
                unchecked((int)attempt.ImageIndex),
                SemaphoreTargetValue: imageCompletionValue,
                SemaphoreCompletedValue: imageCompletionValue,
                QueueFamily: _deviceContext.QueueFamilies.GraphicsFamilyIndex ?? 0U,
                PendingCommandCount: 0,
                ConcurrentWorkerActivity: Volatile.Read(
                    ref _commandRuntime.Workers.ActiveWorkerCount),
                Stage: EVulkanFrameStage.OutputAcquire));

            stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.SampleTimingQueries"))
            {
                VulkanCompletedTimingQueryPools completedQueries =
                    _frameTelemetry.SampleFrameTimingQueries(
                    Api,
                    _deviceContext.Device,
                    unchecked((int)Math.Min(attempt.ImageIndex, int.MaxValue)));
                ResourceRuntime.NotifyTimingQueryPoolsCompleted(completedQueries);
            }

            attempt.Timing.SampleTimingQueries +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);

            stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.ResetDynamicUniformRing"))
            {
                if (MappedFrameArena is { } arena &&
                    !arena.TryResetFrameSlot(
                        attempt.ImageIndex,
                        arena.Generation,
                        submissionCompletionProven:
                            imageCompletionValue != 0))
                {
                    throw new InvalidOperationException(
                        $"Mapped frame-data slot {attempt.ImageIndex} could not be reopened after swapchain-image completion.");
                }
            }

            attempt.Timing.ResetDynamicUniformRing +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);
            attempt.AdvanceTo(EDesktopFramePhase.ImageReady);
        }
    }
}
