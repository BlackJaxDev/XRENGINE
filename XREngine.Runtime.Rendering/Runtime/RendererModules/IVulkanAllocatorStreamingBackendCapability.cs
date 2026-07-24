namespace XREngine.Rendering;

/// <summary>
/// Exposes allocator-pressure information needed by backend-neutral texture streaming policy.
/// </summary>
public interface IVulkanAllocatorStreamingBackendCapability
{
    bool TryGetAllocatorBudgetSnapshot(
        double budgetRatio,
        long reserveBytes,
        out long allocatedBytes,
        out long budgetBytes,
        out long largestHeapBytes,
        out int activeAllocationCount);

    bool IsExpectedImageAllocationDeferral(Exception exception);
}
