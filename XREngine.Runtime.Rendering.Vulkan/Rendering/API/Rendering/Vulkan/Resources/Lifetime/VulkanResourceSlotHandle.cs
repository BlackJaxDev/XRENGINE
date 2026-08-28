namespace XREngine.Rendering.Vulkan;

/// <summary>
/// ABA-safe index into the lifetime tracker's flat native-resource directory.
/// Slot zero and generation zero are reserved as invalid identities.
/// </summary>
internal readonly record struct VulkanResourceSlotHandle(uint Index, ulong Generation)
{
    internal static VulkanResourceSlotHandle Invalid => default;

    internal bool IsValid => Index != 0u && Generation != 0UL;

    public override string ToString()
        => IsValid ? $"slot={Index} generation={Generation}" : "<invalid-resource-slot>";
}
