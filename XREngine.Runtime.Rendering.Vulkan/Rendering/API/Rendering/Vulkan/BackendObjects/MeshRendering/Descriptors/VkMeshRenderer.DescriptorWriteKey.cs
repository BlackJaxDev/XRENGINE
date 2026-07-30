using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    internal readonly record struct DescriptorWriteKey(
        int DescriptorSlotIndex,
        ulong DescriptorSetHandle,
        uint Set,
        uint Binding,
        DescriptorType DescriptorType,
        uint DescriptorCount);
}
