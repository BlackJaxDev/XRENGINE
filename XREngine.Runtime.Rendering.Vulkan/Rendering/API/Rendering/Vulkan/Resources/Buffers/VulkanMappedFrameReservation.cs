namespace XREngine.Rendering.Vulkan;

/// <summary>
/// A stable offset reservation shared by every frame slot in one mapped-frame arena generation.
/// </summary>
internal readonly record struct VulkanMappedFrameReservation(
    ulong Offset,
    uint Length,
    uint Alignment,
    ulong Generation,
    VulkanMappedFrameAllocation? Allocation = null)
{
    internal bool IsValid => Length != 0 && Alignment != 0 && Generation != 0 && Allocation is { IsOpen: true };
}
