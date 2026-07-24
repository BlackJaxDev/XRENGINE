using System;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private bool TryWaitCurrentFrameSlotAndDrainRetiredResources(
            int frameSlot,
            bool interactiveResize,
            string reason)
        {
            if (_frameSlotTimelineValues is not null &&
                frameSlot >= 0 &&
                frameSlot < _frameSlotTimelineValues.Length)
            {
                ulong slotWaitValue = _frameSlotTimelineValues[frameSlot];
                if (interactiveResize && !HasTimelineValueCompleted(_graphicsTimelineSemaphore, slotWaitValue))
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

                WaitForTimelineValue(_graphicsTimelineSemaphore, slotWaitValue);
                SampleFrameTimingQueries(frameSlot);
            }

            DrainInvalidatedCommandBufferRecordings();
            DrainRetiredSwapchainGenerations();
            DrainRetiredDescriptorPools();
            DrainRetiredPipelines();
            DrainRetiredPipelineLayouts();
            DrainRetiredBuffers();
            DrainRetiredFramebuffers();
            DrainRetiredImages();
            return true;
        }

    }
}
