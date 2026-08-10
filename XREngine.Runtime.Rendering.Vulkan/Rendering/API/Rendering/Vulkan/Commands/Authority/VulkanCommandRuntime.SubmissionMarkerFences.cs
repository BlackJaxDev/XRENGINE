using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanCommandRuntime
{
    /// <summary>Rents a CPU-visible fence that resolves from a submitted timeline value.</summary>
    internal VulkanTimelineGpuFence RentTimelineGpuFence()
    {
        lock (Synchronization._submissionMarkerLock)
        {
            VulkanTimelineGpuFence fence = Synchronization._timelineGpuFencePool.Count > 0
                ? Synchronization._timelineGpuFencePool.Pop()
                : new VulkanTimelineGpuFence();
            fence.Reset(Api, DeviceContext, this, ResourceRuntime);
            return fence;
        }
    }
}
