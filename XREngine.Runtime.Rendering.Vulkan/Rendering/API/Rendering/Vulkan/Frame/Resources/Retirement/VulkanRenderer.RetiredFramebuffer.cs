using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Describes a framebuffer deferred until in-flight command buffers release it.
/// </summary>
internal readonly record struct RetiredFramebuffer(
    Framebuffer Framebuffer,
    VulkanRetirementTicket Ticket);
