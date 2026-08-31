using XREngine.Rendering.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>One native receipt set shared by immutable publications and recorded commands.</summary>
internal sealed class VulkanMaterialDescriptorClosureLease : IDisposable
{
    private readonly VulkanDescriptorManager _owner;
    private readonly VulkanBindlessMaterialTextureReceipt[] _receipts;
    private int _released;
    internal VulkanMaterialDescriptorClosureLease? NextReleased;

    private VulkanMaterialDescriptorClosureLease(VulkanDescriptorManager owner,
        VulkanBindlessMaterialTextureReceipt[] receipts, int count,
        ulong tableOwnerId, ulong closureGeneration)
        => (_owner, _receipts, Count, TableOwnerId, ClosureGeneration) =
            (owner, receipts, count, tableOwnerId, closureGeneration);

    internal ulong TableOwnerId { get; }
    internal ulong ClosureGeneration { get; }
    internal int Count { get; }
    internal ReadOnlySpan<VulkanBindlessMaterialTextureReceipt> Receipts => _receipts.AsSpan(0, Count);

    internal static bool TryAcquire(VulkanDescriptorManager owner,
        GPUMaterialTablePublication publication, out VulkanMaterialDescriptorClosureLease? lease, out string reason)
    {
        VulkanBindlessMaterialTextureReceipt[] receipts = new VulkanBindlessMaterialTextureReceipt[
            publication.VulkanTextureReferences.Length];
        if (!owner.TryAcquireGlobalMaterialTextureReceiptLeases(
                publication.VulkanTextureReferences, receipts, out int count, out reason))
        {
            lease = null;
            return false;
        }
        lease = new(owner, receipts, count, publication.OwnerId, publication.DescriptorClosureGeneration);
        return true;
    }

    internal bool Matches(GPUMaterialTablePublication publication)
        => TableOwnerId == publication.OwnerId && ClosureGeneration == publication.DescriptorClosureGeneration;

    // Final ownership can leave under the command tracker lock. Only enqueue
    // here; descriptor-state locks are taken later at a preparation boundary.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _owner.EnqueueReleasedMaterialDescriptorClosure(this);
    }

    internal void ReleaseReceipts() => _owner.ReleaseGlobalMaterialTextureReceiptLeases(Receipts);
}
