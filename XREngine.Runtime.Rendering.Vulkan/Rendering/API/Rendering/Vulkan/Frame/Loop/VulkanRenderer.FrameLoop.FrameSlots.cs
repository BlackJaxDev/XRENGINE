using System;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan
{
    internal sealed unsafe partial class VulkanFrameLoop
    {
        internal EDesktopFrameFlow PrepareDesktopFrameSlot(ref VulkanFrameAttempt attempt)
        {
            long stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.WaitFrameSlot"))
            {
                ulong slotWaitValue =
                    _commandRuntime.Synchronization._frameSlotTimelineValues![attempt.FrameSlot];
                if (attempt.InteractiveResize &&
                    !HasTimelineValueCompleted(
                        _commandRuntime.Synchronization._graphicsTimelineSemaphore,
                        slotWaitValue))
                {
                    DrainSkippedResizeFrameOps(
                        $"Interactive resize frame slot {attempt.FrameSlot} is still busy. TimelineValue={slotWaitValue}");
                    MarkSkippedResizeFrameObserved(attempt.StartTimestamp);
                    VulkanDesktopPreflightOutcome outcome =
                        DesktopWsiOutput.ClassifyPreflight(
                            EVulkanDesktopPreflightStatus
                                .InteractiveSlotBusy);
                    attempt.Stop(
                        outcome.Reason ==
                            EVulkanDesktopPolicyReason
                                .InteractiveSlotBusy
                            ? EDesktopFrameReason.FrameSlotBusy
                            : throw new InvalidOperationException(
                                $"Unexpected interactive slot policy {outcome.Reason}."));
                    return outcome.Flow ==
                        EVulkanDesktopPolicyFlow.Stop
                            ? EDesktopFrameFlow.Stop
                            : throw new InvalidOperationException(
                                $"Unexpected interactive slot flow {outcome.Flow}.");
                }

                WaitForTimelineValue(_commandRuntime.Synchronization._graphicsTimelineSemaphore, slotWaitValue);
            }

            attempt.Timing.WaitFrameSlot +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);

            stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.DrainRetiredResources"))
            {
                _commandRuntime.DrainInvalidatedCommandBufferRecordings(
                    Api, ResourceRuntime);
                OutputRuntime.DrainRetiredDesktopSwapchainGenerations();
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
                    ResourceRuntime.Allocations.Staging.Trim(OutputRuntime);
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
                    WaitForTimelineValue(
                        _commandRuntime.Synchronization._graphicsTimelineSemaphore,
                        imageCompletionValue);
                }
            }

            attempt.Timing.WaitSwapchainImage +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);

            stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.SampleTimingQueries"))
            {
                _frameTelemetry.SampleFrameTimingQueries(
                    Api,
                    _deviceContext.Device,
                    ResourceRuntime,
                    unchecked((int)Math.Min(attempt.ImageIndex, int.MaxValue)));
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
