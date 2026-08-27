namespace XREngine.Rendering.Vulkan;

/// <summary>Preallocated accepted-frame transactions indexed by CPU frame slot.</summary>
internal sealed class VulkanAcceptedFramePlanArena
{
    private readonly VulkanAcceptedFramePlan[] _slots;

    internal VulkanAcceptedFramePlanArena(int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slotCount, 1);
        _slots = new VulkanAcceptedFramePlan[slotCount];
        for (int index = 0; index < _slots.Length; index++)
            _slots[index] = new VulkanAcceptedFramePlan();
    }

    internal VulkanAcceptedFramePlan Begin(
        int frameSlot,
        ulong frameId,
        ulong sceneEpoch,
        in VulkanPresentNowTargetCompatibilityKey compatibility)
    {
        if ((uint)frameSlot >= (uint)_slots.Length)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));
        VulkanAcceptedFramePlan plan = _slots[frameSlot];
        plan.Begin(frameSlot, frameId, sceneEpoch, in compatibility);
        return plan;
    }

    /// <summary>Releases every frame-slot-owned plan publication.</summary>
    internal void ResetAll()
    {
        for (int index = 0; index < _slots.Length; index++)
            _slots[index].Reset();
    }
}
