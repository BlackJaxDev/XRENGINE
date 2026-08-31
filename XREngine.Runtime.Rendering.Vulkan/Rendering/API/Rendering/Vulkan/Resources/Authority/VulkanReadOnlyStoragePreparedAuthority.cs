namespace XREngine.Rendering.Vulkan;

/// <summary>Identifies the exact writable storage epoch used to lower an immutable publication.</summary>
internal readonly record struct VulkanReadOnlyStoragePreparedAuthority(
    VulkanReadOnlyStoragePreparedMap Owner,
    VulkanFrameDataArena Arena,
    ulong ArenaIdentity,
    ulong ArenaGeneration,
    int FrameSlot,
    ulong ResetEpoch)
{
    internal bool IsValid => ArenaIdentity != 0 && ArenaGeneration != 0 && FrameSlot >= 0 && ResetEpoch != 0;
    internal bool IsCurrent => IsValid && Arena.Identity == ArenaIdentity &&
        Arena.Generation == ArenaGeneration && Arena.GetFrameSlotResetEpoch(FrameSlot) == ResetEpoch;
}
