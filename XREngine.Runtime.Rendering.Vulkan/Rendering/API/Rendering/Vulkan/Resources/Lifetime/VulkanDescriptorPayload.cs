using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact native payload published for one descriptor-set binding element.
/// Resource generations disambiguate Vulkan handle reuse across retirements.
/// </summary>
internal readonly record struct VulkanDescriptorPayload(
    DescriptorType DescriptorType,
    ulong PrimaryHandle,
    ulong PrimaryGeneration,
    ulong SecondaryHandle,
    ulong SecondaryGeneration,
    ulong Offset,
    ulong Range,
    ImageLayout ImageLayout);
