namespace XREngine.Rendering.Vulkan;

/// <summary>Root identity carried by telemetry without retaining a renderer reference.</summary>
public readonly record struct VulkanFrameRootIdentity(
    ulong EngineFrameNumber,
    ulong RenderFrameNumber,
    int FrameSlot,
    long StartTimestamp,
    VulkanFrameOutputIdentity Output);
