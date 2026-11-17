using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    /// <summary>
    /// Runs <see cref="VulkanResourceLifetimeTracker.SweepCompletions{TStatus}"/> with native
    /// queries: <c>vkGetSemaphoreCounterValue</c> for a timeline submission and
    /// <c>vkGetFenceStatus</c> for a fence submission. No wait. Returns 0 when the device is not
    /// operational.
    /// </summary>
    internal int SweepLifetimeSubmissionCompletions()
    {
        if (BackendObjectContext is not { IsDeviceOperational: true } context)
            return 0;
        NativeLifetimeSubmissionStatus status = new(context.Api, context.Device);
        return Lifetime.Tracker.SweepCompletions(ref status);
    }

    /// <summary>Queries the native completion of one lifetime submission without waiting.</summary>
    private readonly struct NativeLifetimeSubmissionStatus(Vk api, Device device) : IVulkanLifetimeSubmissionStatus
    {
        public unsafe bool IsComplete(in VulkanLifetimeSubmission submission)
        {
            if (submission.TimelineSemaphoreHandle != 0UL && submission.TimelineValue != 0UL)
            {
                ulong value = 0UL;
                Result result = api.GetSemaphoreCounterValue(device, new Semaphore(submission.TimelineSemaphoreHandle), &value);
                return result == Result.Success && value >= submission.TimelineValue;
            }
            if (submission.FenceHandle != 0UL)
                return api.GetFenceStatus(device, new Fence(submission.FenceHandle)) == Result.Success;
            return false;
        }
    }
}
