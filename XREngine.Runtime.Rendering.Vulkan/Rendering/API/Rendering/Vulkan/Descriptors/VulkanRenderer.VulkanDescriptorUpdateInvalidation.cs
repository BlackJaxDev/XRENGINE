using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Identifies the first ordinary descriptor write in a batch that has live
    /// recorded-command-buffer dependents.
    /// </summary>
    private readonly record struct VulkanDescriptorUpdateInvalidation(
        ulong DescriptorSetHandle,
        uint Binding,
        uint ArrayElement,
        DescriptorType DescriptorType,
        uint DescriptorCount);
}
