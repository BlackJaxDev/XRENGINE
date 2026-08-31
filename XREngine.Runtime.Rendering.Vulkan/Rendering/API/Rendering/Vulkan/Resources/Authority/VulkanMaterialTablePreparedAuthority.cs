namespace XREngine.Rendering.Vulkan;

/// <summary>Completion-safe frame-slot epoch which owns material-table backing selection.</summary>
internal readonly record struct VulkanMaterialTablePreparedAuthority(
    VulkanMaterialTablePreparedMap Owner,
    VulkanFrameDataArena Arena,
    ulong ArenaIdentity,
    ulong ArenaGeneration,
    int FrameSlot,
    ulong ResetEpoch)
{
    internal bool IsCurrent => Owner.IsCurrent(in this);
}
