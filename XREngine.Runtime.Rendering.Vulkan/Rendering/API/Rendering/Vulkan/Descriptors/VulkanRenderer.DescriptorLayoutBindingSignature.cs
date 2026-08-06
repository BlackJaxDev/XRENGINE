using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct DescriptorLayoutBindingSignature(
    uint Set,
    uint Binding,
    DescriptorType DescriptorType,
    uint DescriptorCount,
    ShaderStageFlags StageFlags,
    bool VariableDescriptorCount);
