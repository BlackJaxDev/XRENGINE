using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Represents the signature of a Vulkan descriptor update template, encapsulating the layout, binding, and descriptor information.
    /// </summary>
    /// <param name="DescriptorSetLayout">The Vulkan descriptor set layout associated with this template.</param>
    /// <param name="PipelineLayout">The Vulkan pipeline layout associated with this template.</param>
    /// <param name="BindPoint">The bind point (e.g., graphics or compute) for the descriptor set.</param>
    /// <param name="SetIndex">The index of the descriptor set within the pipeline layout.</param>
    /// <param name="DstBinding">The destination binding within the descriptor set.</param>
    /// <param name="DstArrayElement">The starting array element within the destination binding.</param>
    /// <param name="DescriptorCount">The number of descriptors to update.</param>
    /// <param name="DescriptorType">The type of descriptor being updated.</param>
    /// <param name="Offset">The byte offset within the update template.</param>
    /// <param name="Stride">The byte stride between consecutive descriptors in the update template.</param>
    private readonly record struct DescriptorUpdateTemplateSignature(
        ulong DescriptorSetLayout,
        ulong PipelineLayout,
        int BindPoint,
        uint SetIndex,
        uint DstBinding,
        uint DstArrayElement,
        uint DescriptorCount,
        DescriptorType DescriptorType,
        nuint Offset,
        nuint Stride);
}
