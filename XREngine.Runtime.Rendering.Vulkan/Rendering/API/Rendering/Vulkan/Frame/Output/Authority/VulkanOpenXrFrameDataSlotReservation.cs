namespace XREngine.Rendering.Vulkan;

/// <summary>Reserves the desktop-indexed and OpenXR eye frame-data slots required by one output generation.</summary>
internal sealed class VulkanOpenXrFrameDataSlotReservation(
    VulkanOutputRuntime output,
    VulkanResourceRuntime resources,
    VulkanCommandRuntime commands)
{
    internal int ReserveForDesktopImageCount(int desktopImageCount)
    {
        int desktopSlots = Math.Max(Math.Max(desktopImageCount, 2), 1);
        int totalSlots = checked(desktopSlots + output.OpenXrBackend.EyeFrameDataSlotCount);
        resources.Descriptors.EnsureFrameSlotCountFloor(totalSlots);
        commands.EnsureFrameDataSlotCapacity(totalSlots);
        return totalSlots;
    }
}
