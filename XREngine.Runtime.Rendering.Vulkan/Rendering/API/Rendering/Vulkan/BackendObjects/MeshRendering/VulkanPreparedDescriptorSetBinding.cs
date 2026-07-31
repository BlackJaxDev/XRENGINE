using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One descriptor-set bind resolved before a command-chain worker is released.
/// Dynamic offsets reference a contiguous range in the owning prepared draw.
/// </summary>
internal readonly record struct VulkanPreparedDescriptorSetBinding(
    DescriptorSet DescriptorSet,
    uint SetIndex,
    int DynamicOffsetStart,
    int DynamicOffsetCount);
