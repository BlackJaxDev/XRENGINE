using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Timeline-semaphore wait required before an acquire-side ownership barrier.
/// </summary>
internal readonly record struct VulkanQueueSemaphoreRequirement(
    ulong SemaphoreHandle,
    ulong Value,
    PipelineStageFlags2 WaitStageMask,
    uint SourceQueueFamilyIndex,
    uint DestinationQueueFamilyIndex)
{
    public bool IsValid =>
        SemaphoreHandle != 0 &&
        Value != 0 &&
        SourceQueueFamilyIndex != DestinationQueueFamilyIndex;

    public bool IsSatisfiedBy(
        ulong semaphoreHandle,
        ulong value,
        PipelineStageFlags2 waitStageMask)
    {
        if (!IsValid ||
            semaphoreHandle != SemaphoreHandle ||
            value < Value)
        {
            return false;
        }

        return (waitStageMask & PipelineStageFlags2.AllCommandsBit) != 0 ||
               (waitStageMask & WaitStageMask) == WaitStageMask;
    }
}
