namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Retained flat command vector for one accepted sealed submission. The submit
/// gateway owns this receipt while it is serialized, so the normal stable path
/// can publish and release the exact ABA-safe slots without returning to the
/// command-buffer lifetime dictionaries.
/// </summary>
internal sealed class VulkanSealedSubmissionBatchReceipt
{
    internal const int Capacity = 16;

    private readonly Entry[] _entries = new Entry[Capacity];

    internal int Count { get; private set; }

    internal bool IsActive => Count != 0;

    internal ref readonly Entry GetEntry(int index) => ref _entries[index];

    internal bool Begin(int count)
    {
        if (count <= 0 || count > _entries.Length || IsActive)
            return false;

        Count = count;
        return true;
    }

    internal void Set(
        int index,
        SealedSubmissionContract contract,
        VulkanCommandBufferLifetimeRecord lifetime,
        VulkanCommandBufferTrackingBatch? trackingBatch)
        => _entries[index] = new Entry(contract, lifetime, trackingBatch);

    internal void Clear()
    {
        Array.Clear(_entries, 0, Count);
        Count = 0;
    }

    internal readonly record struct Entry(
        SealedSubmissionContract Contract,
        VulkanCommandBufferLifetimeRecord Lifetime,
        VulkanCommandBufferTrackingBatch? TrackingBatch);
}
