using System;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanFrameLoop
    {
        private bool TryWaitCurrentFrameSlotAndDrainRetiredResources(
            int frameSlot,
            bool interactiveResize,
            string reason)
        {
            if (_commandRuntime.Synchronization._frameSlotTimelineValues is not null &&
                frameSlot >= 0 &&
                frameSlot < _commandRuntime.Synchronization._frameSlotTimelineValues.Length)
            {
                ulong slotWaitValue = _commandRuntime.Synchronization._frameSlotTimelineValues[frameSlot];
                if (interactiveResize && !HasTimelineValueCompleted(_commandRuntime.Synchronization._graphicsTimelineSemaphore, slotWaitValue))
                {
                    Debug.VulkanEvery(
                        $"Vulkan.Frame.{GetHashCode()}.InteractiveResizeBusySlot",
                        TimeSpan.FromMilliseconds(500),
                        "[Vulkan] Skipping retired-resource cleanup during interactive resize because frame slot {0} is still busy. Reason={1} TimelineValue={2}",
                        frameSlot,
                        reason,
                        slotWaitValue);
                    return false;
                }

                WaitForTimelineValue(_commandRuntime.Synchronization._graphicsTimelineSemaphore, slotWaitValue);
                VulkanCompletedTimingQueryPools completedQueries =
                    _frameTelemetry.SampleFrameTimingQueries(
                    Api,
                    _deviceContext.Device,
                    frameSlot);
                ResourceRuntime.NotifyTimingQueryPoolsCompleted(completedQueries);
            }

            _commandRuntime.DrainInvalidatedCommandBufferRecordings(
                Api, ResourceRuntime);
            _commandRuntime.DrainRetiredSynchronousSubmissions();
            DrainRetiredDesktopSwapchainGenerations();
            _commandRuntime.DrainRetiredCommandBuffers(
                Api,
                _deviceContext.Device,
                ResourceRuntime,
                frameSlot);
            _commandRuntime.DrainRetiredCommandPools(
                Api,
                _deviceContext.Device,
                ResourceRuntime,
                frameSlot);
            ResourceRuntime.DrainRetiredDescriptorSets(
                Api, _deviceContext.Device, frameSlot);
            ResourceRuntime.DrainRetiredDescriptorPools(
                Api, _deviceContext.Device, frameSlot);
            ResourceRuntime.DrainRetiredPipelines(
                Api, _deviceContext.Device, frameSlot);
            ResourceRuntime.DrainRetiredPipelineLayouts(
                Api, _deviceContext.Device, frameSlot);
            ResourceRuntime.DrainRetiredDescriptorSetLayouts(
                Api, _deviceContext.Device, frameSlot);
            int pooledBuffers = ResourceRuntime.DrainRetiredBuffers(
                Api,
                _deviceContext.Device,
                _frameTelemetry,
                frameSlot);
            if (pooledBuffers != 0)
                ResourceRuntime.Allocations.Staging.Trim(
                    ResourceRuntime.BackendObjectContext ?? throw new InvalidOperationException(
                        "The Vulkan backend object context is not initialized."));
            ResourceRuntime.DrainRetiredFramebuffers(
                Api, _deviceContext.Device, frameSlot);
            ResourceRuntime.DrainRetiredImages(
                Api, _deviceContext.Device, frameSlot);
            return true;
        }

    }
}
