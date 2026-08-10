namespace XREngine.Rendering.Vulkan;

/// <summary>Host/device ownership state for one stream chunk in one frame slot.</summary>
internal enum VulkanFrameDataArenaSlotState : byte
{
    Writable,
    Prepared,
    Submitted,
    Invalid,
}
