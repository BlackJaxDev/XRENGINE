namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Generation-tagged identity for a sealed command-buffer submission record.
/// A slot is never trusted after it has been tombstoned or reused.
/// </summary>
internal readonly record struct VulkanStableCommandSlotHandle(uint Index, ulong Generation)
{
    internal static VulkanStableCommandSlotHandle Invalid => default;

    internal bool IsValid => Index != 0 && Generation != 0;
}
