using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Describes buffer handles deferred until the frame slot's GPU work completes.
/// </summary>
internal readonly record struct RetiredBuffer(
    Silk.NET.Vulkan.Buffer Buffer,
    DeviceMemory Memory,
    VulkanRetirementTicket Ticket);
