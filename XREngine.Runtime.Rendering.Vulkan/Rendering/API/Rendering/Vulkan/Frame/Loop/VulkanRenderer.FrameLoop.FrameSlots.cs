using System;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private EDesktopFrameFlow PrepareDesktopFrameSlot(ref VulkanFrameAttempt attempt)
        {
            long stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.WaitFrameSlot"))
            {
                ulong slotWaitValue =
                    _frameSlotTimelineValues![attempt.FrameSlot];
                if (attempt.InteractiveResize &&
                    !HasTimelineValueCompleted(
                        _graphicsTimelineSemaphore,
                        slotWaitValue))
                {
                    DrainSkippedResizeFrameOps(
                        $"Interactive resize frame slot {attempt.FrameSlot} is still busy. TimelineValue={slotWaitValue}");
                    MarkSkippedResizeFrameObserved(attempt.StartTimestamp);
                    VulkanDesktopPreflightOutcome outcome =
                        DesktopWsiTarget.ClassifyPreflight(
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

                WaitForTimelineValue(_graphicsTimelineSemaphore, slotWaitValue);
            }

            attempt.Timing.WaitFrameSlot +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);

            stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.DrainRetiredResources"))
            {
                DrainInvalidatedCommandBufferRecordings();
                DrainRetiredSwapchainGenerations();
                DrainRetiredCommandBuffers(attempt.FrameSlot);
                DrainRetiredCommandPools(attempt.FrameSlot);
                DrainRetiredDescriptorSets(attempt.FrameSlot);
                DrainRetiredDescriptorPools();
                DrainRetiredPipelines();
                DrainRetiredPipelineLayouts();
                DrainRetiredDescriptorSetLayouts();
                DrainRetiredQueryPools(attempt.FrameSlot);
                DrainRetiredBufferViews(attempt.FrameSlot);
                DrainRetiredBuffers();
                DrainRetiredFramebuffers();
                DrainRetiredImages();
                DrainCompletedRecordedTextureUploadPublications();
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
                    swapChainExtent.Width,
                    swapChainExtent.Height);
            }

            attempt.AdvanceTo(EDesktopFramePhase.SlotReady);
            return EDesktopFrameFlow.Continue;
        }

        private void PrepareAcquiredDesktopImage(ref VulkanFrameAttempt attempt)
        {
            ThrowIfDesktopFrameFaultInjected(
                EVulkanDesktopFrameFaultPoint.ImagePreparation);
            long stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.WaitSwapchainImage"))
            {
                if (_swapchainImageTimelineValues is not null &&
                    attempt.ImageIndex < _swapchainImageTimelineValues.Length)
                {
                    WaitForTimelineValue(
                        _graphicsTimelineSemaphore,
                        _swapchainImageTimelineValues[attempt.ImageIndex]);
                }
            }

            attempt.Timing.WaitSwapchainImage +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);

            stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.SampleTimingQueries"))
            {
                SampleFrameTimingQueries(
                    unchecked((int)Math.Min(attempt.ImageIndex, int.MaxValue)));
            }

            attempt.Timing.SampleTimingQueries +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);

            stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.ResetDynamicUniformRing"))
            {
                ResetDynamicUniformRingBuffer(attempt.ImageIndex);
            }

            attempt.Timing.ResetDynamicUniformRing +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);
            attempt.AdvanceTo(EDesktopFramePhase.ImageReady);
        }
    }
}
