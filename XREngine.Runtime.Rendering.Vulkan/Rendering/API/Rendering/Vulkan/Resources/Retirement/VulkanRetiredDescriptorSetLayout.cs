using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanRetiredDescriptorSetLayout(
    DescriptorSetLayout DescriptorSetLayout,
    VulkanRetirementTicket Ticket,
    string Owner);
