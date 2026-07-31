namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Persistent producer/consumer storage for one OpenXR eye's reusable
/// frame-data refresh requests. Publication is rejected while a worker owns a
/// read lease.
/// </summary>
internal sealed class VulkanOpenXrFrameDataRefreshRequestStorage
{
    private readonly object _sync = new();
    private VulkanReusableFrameDataRefreshRequest[] _requests = [];
    private VulkanReusableFrameDataRefreshRequest[] _ownerWorkRequests = [];
    private int _count;
    private int _ownerWorkCount;
    private VulkanReusableFrameDataRefreshBatchInfo _batchInfo;
    private ulong _generation;
    private int _readers;

    internal VulkanOpenXrFrameDataRefreshRequestLease Publish(
        ReadOnlySpan<VulkanReusableFrameDataRefreshRequest> requests,
        ReadOnlySpan<VulkanReusableFrameDataRefreshRequest> ownerWorkRequests,
        in VulkanReusableFrameDataRefreshBatchInfo batchInfo)
    {
        lock (_sync)
        {
            if (_readers != 0)
            {
                throw new InvalidOperationException(
                    "Cannot replace OpenXR frame-data refresh requests while a worker owns a read lease.");
            }

            EnsureCapacity(ref _requests, requests.Length);
            EnsureCapacity(ref _ownerWorkRequests, ownerWorkRequests.Length);
            requests.CopyTo(_requests);
            if (_count > requests.Length)
            {
                Array.Clear(
                    _requests,
                    requests.Length,
                    _count - requests.Length);
            }

            ownerWorkRequests.CopyTo(_ownerWorkRequests);
            if (_ownerWorkCount > ownerWorkRequests.Length)
            {
                Array.Clear(
                    _ownerWorkRequests,
                    ownerWorkRequests.Length,
                    _ownerWorkCount - ownerWorkRequests.Length);
            }

            _count = requests.Length;
            _ownerWorkCount = ownerWorkRequests.Length;
            _batchInfo = batchInfo;
            _generation = VulkanGeneration.NextNonZero(_generation);
            return new VulkanOpenXrFrameDataRefreshRequestLease(
                this,
                _generation);
        }
    }

    internal bool TryAcquire(
        ulong generation,
        out ReadOnlySpan<VulkanReusableFrameDataRefreshRequest> requests,
        out ReadOnlySpan<VulkanReusableFrameDataRefreshRequest>
            ownerWorkRequests,
        out VulkanReusableFrameDataRefreshBatchInfo batchInfo)
    {
        lock (_sync)
        {
            if (generation == 0 || generation != _generation)
            {
                requests = default;
                ownerWorkRequests = default;
                batchInfo = default;
                return false;
            }

            _readers++;
            requests = _requests.AsSpan(0, _count);
            ownerWorkRequests =
                _ownerWorkRequests.AsSpan(0, _ownerWorkCount);
            batchInfo = _batchInfo;
            return true;
        }
    }

    internal void Release()
    {
        lock (_sync)
        {
            if (_readers <= 0)
            {
                throw new InvalidOperationException(
                    "OpenXR frame-data refresh request read lease was released without an owner.");
            }

            _readers--;
        }
    }

    private static void EnsureCapacity(
        ref VulkanReusableFrameDataRefreshRequest[] requests,
        int required)
    {
        if (requests.Length >= required)
            return;

        int capacity = Math.Max(
            required,
            Math.Max(16, requests.Length * 2));
        Array.Resize(ref requests, capacity);
    }
}
