namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Retains the exact flat resource vector pinned at queue admission. The same
/// receipt is consumed by successful-submission publication and cleanup, so a
/// mutable command dependency list can never redirect or unbalance pins.
/// </summary>
internal sealed class VulkanSubmissionPinReceipt
{
    private const int InitialResourceCapacity = 64;

    private VulkanResourceSlotHandle[] _resources =
        new VulkanResourceSlotHandle[InitialResourceCapacity];
    private VulkanResourceLifetimeRecord?[] _resourceRecords =
        new VulkanResourceLifetimeRecord?[InitialResourceCapacity];
    private int _resourceCount;

    internal VulkanResourceSlotHandle CommandBufferSlot { get; private set; }
    internal VulkanResourceLifetimeRecord? CommandBufferResource { get; private set; }

    internal bool IsActive { get; private set; }

    internal ReadOnlySpan<VulkanResourceSlotHandle> Resources
        => _resources.AsSpan(0, _resourceCount);
    internal ReadOnlySpan<VulkanResourceLifetimeRecord?> ResourceRecords
        => _resourceRecords.AsSpan(0, _resourceCount);

    /// <summary>
    /// Reserves cold-path capacity while a command contract is sealed. Stable
    /// submission hits therefore only copy already allocated slot values.
    /// </summary>
    internal void EnsureCapacity(int resourceCount)
    {
        if (resourceCount <= _resources.Length)
            return;

        int capacity = _resources.Length;
        do
            capacity = checked(capacity * 2);
        while (resourceCount > capacity);

        Array.Resize(ref _resources, capacity);
        Array.Resize(ref _resourceRecords, capacity);
    }

    internal bool TryCapture(
        VulkanResourceSlotHandle commandBufferSlot,
        VulkanResourceLifetimeRecord commandBufferResource,
        ReadOnlySpan<VulkanSealedResourceDependency> resources)
    {
        if (IsActive || !commandBufferSlot.IsValid || commandBufferResource is null)
            return false;

        EnsureCapacity(resources.Length);
        for (int index = 0; index < resources.Length; ++index)
        {
            VulkanResourceSlotHandle slot = resources[index].Slot;
            VulkanResourceLifetimeRecord? resource = resources[index].Resource;
            if (!slot.IsValid || resource is null)
            {
                Clear();
                return false;
            }

            _resources[index] = slot;
            _resourceRecords[index] = resource;
        }

        CommandBufferSlot = commandBufferSlot;
        CommandBufferResource = commandBufferResource;
        _resourceCount = resources.Length;
        IsActive = true;
        return true;
    }

    internal bool TryCaptureNoLock(
        VulkanResourceLifetimeTracker tracker,
        VulkanResourceSlotHandle commandBufferSlot,
        IReadOnlyList<KeyValuePair<VulkanResourceLifetimeKey, ulong>> resources)
    {
        if (IsActive || !commandBufferSlot.IsValid)
            return false;

        EnsureCapacity(resources.Count);
        for (int index = 0; index < resources.Count; ++index)
        {
            KeyValuePair<VulkanResourceLifetimeKey, ulong> dependency =
                resources[index];
            if (!tracker.TryGetResourceSlotNoLock(
                    dependency.Key,
                    out VulkanResourceSlotHandle slot) ||
                slot.Generation != dependency.Value)
            {
                Clear();
                return false;
            }

            _resources[index] = slot;
            if (!tracker.TryResolveResourceSlotNoLock(
                    slot,
                    out VulkanResourceLifetimeRecord resource))
            {
                Clear();
                return false;
            }
            _resourceRecords[index] = resource;
        }

        CommandBufferSlot = commandBufferSlot;
        if (!tracker.TryResolveResourceSlotNoLock(
                commandBufferSlot,
                out VulkanResourceLifetimeRecord commandBufferResource))
        {
            Clear();
            return false;
        }
        CommandBufferResource = commandBufferResource;
        _resourceCount = resources.Count;
        IsActive = true;
        return true;
    }

    internal void Clear()
    {
        CommandBufferSlot = VulkanResourceSlotHandle.Invalid;
        CommandBufferResource = null;
        Array.Clear(_resourceRecords, 0, _resourceCount);
        _resourceCount = 0;
        IsActive = false;
    }
}
