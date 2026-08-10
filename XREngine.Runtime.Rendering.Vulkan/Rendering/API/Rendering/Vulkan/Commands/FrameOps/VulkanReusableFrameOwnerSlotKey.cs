namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies one frequency-owned publication range in one mapped frame slot.
/// Content generation is the dictionary value so ordinary camera motion
/// replaces state instead of accumulating keys.
/// </summary>
internal readonly record struct VulkanReusableFrameOwnerSlotKey(
    ulong PublicationLayoutSignature,
    EVulkanBindingFrequency Frequency,
    ulong OwnerIdentity,
    uint FrameSlot);
