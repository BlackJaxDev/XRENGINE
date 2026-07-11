using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct DescriptorLayoutBindingSignature(
        uint Set,
        uint Binding,
        DescriptorType DescriptorType,
        uint DescriptorCount,
        ShaderStageFlags StageFlags,
        bool VariableDescriptorCount);
}
