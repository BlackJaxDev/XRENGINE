using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct RetiredBufferView(
    BufferView BufferView,
    VulkanRenderer.VulkanRetirementTicket Ticket);
