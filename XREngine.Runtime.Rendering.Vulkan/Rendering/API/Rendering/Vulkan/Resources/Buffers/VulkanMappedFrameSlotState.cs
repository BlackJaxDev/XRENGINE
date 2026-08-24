namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Atomic host/device ownership state for one mapped frame-arena slot.
/// </summary>
internal enum VulkanMappedFrameSlotState : byte
{
    Writable = 0,
    Prepared = 1,
    Submitted = 2,
    Invalid = 3,
}
