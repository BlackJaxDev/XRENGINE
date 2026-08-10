using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Deferred native pipeline-layout destruction with exact completion proof.</summary>
internal readonly record struct VulkanRetiredPipelineLayout(
    PipelineLayout PipelineLayout,
    VulkanRetirementTicket Ticket,
    string Owner);
