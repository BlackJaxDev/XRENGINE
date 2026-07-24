namespace XREngine.Rendering.Vulkan;

public partial class VulkanRenderer : IVulkanAllocatorStreamingBackendCapability
{
    bool IVulkanAllocatorStreamingBackendCapability.TryGetAllocatorBudgetSnapshot(
        double budgetRatio,
        long reserveBytes,
        out long allocatedBytes,
        out long budgetBytes,
        out long largestHeapBytes,
        out int activeAllocationCount)
        => TryGetVulkanAllocatorBudgetSnapshot(
            budgetRatio,
            reserveBytes,
            out allocatedBytes,
            out budgetBytes,
            out largestHeapBytes,
            out activeAllocationCount);

    bool IVulkanAllocatorStreamingBackendCapability.IsExpectedImageAllocationDeferral(Exception exception)
        => IsExpectedVulkanImageAllocationDeferral(exception);
}
