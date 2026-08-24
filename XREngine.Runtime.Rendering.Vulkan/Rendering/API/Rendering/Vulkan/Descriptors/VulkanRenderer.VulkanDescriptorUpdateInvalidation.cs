using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies the first ordinary descriptor write in a batch that has live
/// recorded-command-buffer dependents.
/// </summary>
internal readonly record struct VulkanDescriptorUpdateInvalidation(
    ulong DescriptorSetHandle,
    uint Binding,
    uint ArrayElement,
    DescriptorType DescriptorType,
    uint DescriptorCount,
    string? Owner);
