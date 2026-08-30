namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free monotonic snapshot for the desktop submit/present boundary
/// and the two shared tracking locks used by sealed Vulkan submission.
/// </summary>
public readonly record struct VulkanFrameHotPathSnapshot(
    long SubmissionInvocationCount,
    long SubmissionAllocatedBytes,
    long SubmissionAllocationHighWaterBytes,
    long PresentInvocationCount,
    long PresentAllocatedBytes,
    long PresentAllocationHighWaterBytes,
    long LifetimeLockWaitCount,
    long LifetimeLockWaitTicks,
    long LifetimeLockWaitPeakTicks,
    long LifetimeLockWaitOverThresholdCount,
    long LayoutLockWaitCount,
    long LayoutLockWaitTicks,
    long LayoutLockWaitPeakTicks,
    long LayoutLockWaitOverThresholdCount,
    long LockWaitThresholdTicks)
{
    public bool HasNoManagedAllocationsSince(
        in VulkanFrameHotPathSnapshot baseline)
        => SubmissionAllocatedBytes == baseline.SubmissionAllocatedBytes &&
           PresentAllocatedBytes == baseline.PresentAllocatedBytes;

    public bool HasNoOverThresholdLockWaitsSince(
        in VulkanFrameHotPathSnapshot baseline)
        => LifetimeLockWaitOverThresholdCount ==
               baseline.LifetimeLockWaitOverThresholdCount &&
           LayoutLockWaitOverThresholdCount ==
               baseline.LayoutLockWaitOverThresholdCount;
}
