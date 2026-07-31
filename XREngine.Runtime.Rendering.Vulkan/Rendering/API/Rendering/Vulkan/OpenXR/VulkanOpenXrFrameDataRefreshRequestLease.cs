namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies one immutable publication in an eye-owned reusable refresh
/// request buffer.
/// </summary>
internal readonly record struct VulkanOpenXrFrameDataRefreshRequestLease(
    VulkanOpenXrFrameDataRefreshRequestStorage? Owner,
    ulong Generation)
{
    internal bool TryAcquire(
        out ReadOnlySpan<VulkanReusableFrameDataRefreshRequest> requests,
        out ReadOnlySpan<VulkanReusableFrameDataRefreshRequest>
            ownerWorkRequests,
        out VulkanReusableFrameDataRefreshBatchInfo batchInfo)
    {
        if (Owner is not null)
        {
            return Owner.TryAcquire(
                Generation,
                out requests,
                out ownerWorkRequests,
                out batchInfo);
        }

        requests = default;
        ownerWorkRequests = default;
        batchInfo = default;
        return false;
    }

    internal void Release()
        => Owner?.Release();
}
