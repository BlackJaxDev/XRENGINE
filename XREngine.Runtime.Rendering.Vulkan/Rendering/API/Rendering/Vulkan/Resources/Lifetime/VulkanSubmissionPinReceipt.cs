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
    private int _resourceCount;

    internal VulkanResourceSlotHandle CommandBufferSlot { get; private set; }

    internal bool IsActive { get; private set; }

    internal ReadOnlySpan<VulkanResourceSlotHandle> Resources
        => _resources.AsSpan(0, _resourceCount);

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
    }

    internal bool TryCapture(
        VulkanResourceSlotHandle commandBufferSlot,
        ReadOnlySpan<VulkanSealedResourceDependency> resources)
    {
        if (IsActive || !commandBufferSlot.IsValid)
            return false;

        EnsureCapacity(resources.Length);
        for (int index = 0; index < resources.Length; ++index)
        {
            VulkanResourceSlotHandle slot = resources[index].Slot;
            if (!slot.IsValid)
            {
                Clear();
                return false;
            }

            _resources[index] = slot;
        }

        CommandBufferSlot = commandBufferSlot;
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
        }

        CommandBufferSlot = commandBufferSlot;
        _resourceCount = resources.Count;
        IsActive = true;
        return true;
    }

    internal void Clear()
    {
        CommandBufferSlot = VulkanResourceSlotHandle.Invalid;
        _resourceCount = 0;
        IsActive = false;
    }
}
