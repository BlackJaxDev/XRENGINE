namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Backend-neutral identity for the native Vulkan present mode selected by a
/// desktop presentation profile.
/// </summary>
public enum EVulkanPresentMode
{
    Unknown,
    Fifo,
    Mailbox,
    Immediate,
    FifoRelaxed,
}
